using Lib.render._dx11.api;
using SharpDX;
using SharpDX.Direct3D11;
using T3.Core.DataTypes.ShaderGraph;
using T3.Core.Stats;
using Utilities = T3.Core.Utils.Utilities;

namespace Lib.field.render;

/** *
 * Handles triggering the generation of shader graph code and updating the float parameters.
 *
 * Also see <see cref="ShaderGraphNode"/> for more documentation.
 * 
 *
 * The focus of this is to avoid all unnecessary updates or even recompilations. For this we
 * follow the following strategy:
 *
 * ## Field is not dirty:
 * No Updates necessary. All parameters code and graph are already up to date.
 * 
 * ## Field is dirty:
 * Update the invalidated part of connected FieldGraph-instances and their GraphNodes. Each GraphNode
 * will fetch is updated float parameters. It will try to detected changes in the graph setup by comparing
 * if the connected graph nodes have changed.
 *
 * They will recursively return an invalidate settings for CodeNeedsUpdate and ParamsNeedUpdate. 
 *
 * If CodeNeedsUpdate is true, the shader code will be updated.
 */
[Guid("73c028d1-3de2-4269-b503-97f62bbce320")]
internal sealed class GenerateShaderGraphCode : Instance<GenerateShaderGraphCode>
,IStatusProvider
{
    [Output(Guid = "A1AB0C16-ED15-4334-A529-10E3C217DF1A")]
    public readonly Slot<string> ShaderCode = new();

    [Output(Guid = "1a9b5e15-e9a7-4ed4-aa1a-2072398921b4")]
    public readonly Slot<Buffer> FloatParams = new();

    // This is a hack so the additional resources can be shared to ops like [SetVertexAndPixelShaderStage]
    [Output(Guid = "ADF247CD-79CC-4D4E-B3C1-6A8B2D54683D")]
    public readonly Slot<Object> Resources = new();
    
    public GenerateShaderGraphCode()
    {
        ShaderCode.UpdateAction += Update;
        _graphId = GetHashCode();
    }

    
    /// <summary>
    /// This is only updated if subgraph has been changed.
    /// </summary>
    private void Update(EvaluationContext context)
    {
        _lastErrorMessage = null;
        
        var hasTemplateChanged = TemplateCode.DirtyFlag.IsDirty;

        var definesAreDirty = AdditionalDefines.DirtyFlag.IsDirty;
        if (definesAreDirty)
        {
            var additionalDefines = AdditionalDefines.GetValue(context);
            if (additionalDefines != _additionalDefines)
            {
                _additionalDefines = additionalDefines;
                hasTemplateChanged= true;
            }
        }
        
        var templateCode = TemplateCode.GetValue(context);
        if (string.IsNullOrEmpty(templateCode))
        {
            this.LogErrorState("Missing input template code");
            return;
        }

        if (_needsInvalidation)
        {
            _needsInvalidation = false;
        }

        _needsInvalidation = hasTemplateChanged;
        
        // Recursively update complete shader graph and collect changes
        _graphNode = Field.GetValue(context);

        if (_graphNode == null)
        {
            _needsInvalidation = true;
            ShaderCode.Value = templateCode;
            return;
        }
        
        var changes = _graphNode.CollectedChanges;
        if (_graphNode.StructureHash != _lastStructureHash)
        {
            _lastStructureHash = _graphNode.StructureHash;
            changes |= ShaderGraphNode.ChangedFlags.Structural;
        }
        
        if (changes == ShaderGraphNode.ChangedFlags.None && !hasTemplateChanged)
            return;

        AssembleParams();

        AssembleResources();
        Resources.Value = _resourceViews;
        
        var floatParams = AllFloatValues;
        if (floatParams.Count > 0)
        {
            CreateParameterBuffer(FloatParams, floatParams);
        }
        
        if (hasTemplateChanged || (changes & (ShaderGraphNode.ChangedFlags.Code|ShaderGraphNode.ChangedFlags.Structural)) != 0)
        {
            ShaderCode.Value = GenerateShaderCode(_additionalDefines + "\n\n" + templateCode);    // Should probably use a string builder here...
        }
        
        _updateCycleCount++;
    }

    private string GenerateShaderCode(string templateCode)
    {
        AssembleAndInjectParameters(ref templateCode);
        AssembleAndInjectCode(ref templateCode);
        InjectResourcesCode(ref templateCode);
        return templateCode;
    }

    private void AssembleAndInjectParameters(ref string templateCode)
    {
        var commentHook = ToHlslTemplateTag("FLOAT_PARAMS");
        if (!templateCode.Contains(commentHook))
            return;

        var sb = new StringBuilder();
        foreach (var name in _allShaderCodeParams)
        {
            sb.AppendLine($"\t{name.ShaderTypeName}  {name.Name};");
        }

        templateCode = templateCode.Replace(commentHook, sb.ToString());
    }
    
    /**
     * Before generating the complete shader, we need to collect all parameters of all nodes
     * into to lists: One for the float constant buffer and one of parameter names and definition
     */
    private void AssembleParams()
    {
        _allFloatParameterValues.Clear();
        _allShaderCodeParams.Clear();

        _graphNode.CollectAllNodeParams(_allFloatParameterValues, _allShaderCodeParams, _updateCycleCount, _graphId);
    }

    // Should change if resources structure changed
    private void AssembleResources()
    {
        _resourceReferences.Clear();
        _graphNode.CollectResources(_resourceReferences, _updateCycleCount,_graphId);
        _resourceViews.Clear();
        foreach (var r in _resourceReferences)
        {
            _resourceViews.Add(r.Srv);
        }
    }
    
    private readonly List<ShaderGraphNode.SrvBufferReference> _resourceReferences = [];
    private readonly List<ShaderResourceView> _resourceViews = [];
    
    private readonly List<ShaderParamHandling.ShaderCodeParameter> _allShaderCodeParams = [];
    private IReadOnlyList<float> AllFloatValues => _allFloatParameterValues;
    private readonly List<float> _allFloatParameterValues = [];
    private int _lastStructureHash;
    private readonly int _graphId;

    private void AssembleAndInjectCode(ref string templateCode)
    {
        _codeAssembleContext.Reset();
        
        // Recursively collect code from connected nodes
        _graphNode.CollectEmbeddedShaderCode(_codeAssembleContext);
        
        // Build and inject definitions (must be injected before resources to support custom struct type definition)
        _definitionsBuilder.Clear();
        
        _definitionsBuilder.AppendLine("// --- globals -------------------");
        foreach (var code in _codeAssembleContext.Globals.Values)
        {
            _definitionsBuilder.AppendLine(code);
            _definitionsBuilder.AppendLine("");

        }
        _definitionsBuilder.AppendLine(_codeAssembleContext.Definitions.ToString());
        TryInject("GLOBALS", ref templateCode, _definitionsBuilder.ToString());
        
        // Build and inject functions...
        _functionsBuilder.Clear();
        _functionsBuilder.AppendLine("// --- instance functions -------------------");
        TryInject("FIELD_FUNCTIONS", ref templateCode, _functionsBuilder.ToString());
        
        // Build and inject calls...
        TryInject("FIELD_CALL", ref templateCode, _codeAssembleContext.Calls.ToString());
    }

    /// <summary>
    /// Injects resource definitions starting with the giving index defined by the look like...
    /// <code>
    /// /*{RESOURCES(t6)}*/
    /// </code>
    /// with definitions like...
    /// <code>
    /// StructuredBuffer&lt;Point&gt; Points : register(t6);
    /// </code>
    /// 
    /// </summary>
    /// <param name="templateCode"></param>
    private void InjectResourcesCode(ref string templateCode)
    {
        const string resourcesStartHook = "/*{RESOURCES(";

        var resourceHookIndex = templateCode.IndexOf(resourcesStartHook, StringComparison.Ordinal);
        if (resourceHookIndex == -1)
            return;

        var subIndex = resourceHookIndex + resourcesStartHook.Length;
        const string resourcesEndHook = ")}*/";
        var resourceHookEndIndex = templateCode.IndexOf(resourcesEndHook, resourceHookIndex, StringComparison.Ordinal);
        if (resourceHookEndIndex == -1)
            return;

        var t = templateCode[subIndex];
        var indexSpan = templateCode.AsSpan(subIndex + 1, resourceHookEndIndex - subIndex-1);
        var xxx = indexSpan.ToString();
        if (!int.TryParse(xxx, out var index))
            return;

        _resourceDefinitionsBuilder.Clear();
        foreach (var rr in _resourceReferences)
        {
            _resourceDefinitionsBuilder.AppendLine($"{rr.Definition}:register({t}{index});");
            index++;
        }

        var resourceHook = templateCode.AsSpan(resourceHookIndex, resourceHookEndIndex + resourcesEndHook.Length- resourceHookIndex ).ToString();
        templateCode = templateCode.Replace(resourceHook, _resourceDefinitionsBuilder.ToString());
    }
    


    private static void TryInject(string hookId, ref string code, string insert)
    {
        var hookString = ToHlslTemplateTag(hookId);

        // Avoid ArgumentException
        if (string.IsNullOrEmpty(code))
            return;
        
        code = code.Replace(hookString, insert);
    } 

    private readonly CodeAssembleContext _codeAssembleContext = new();
    private int _updateCycleCount;
    private static readonly StringBuilder _definitionsBuilder = new();
    private static readonly StringBuilder _functionsBuilder = new();
    private static readonly StringBuilder _resourceDefinitionsBuilder = new();
    
    private static void CreateParameterBuffer(Slot<Buffer> floatSlotBuffer, IReadOnlyList<float> floatParams)
    {
        try
        {
            if (floatParams == null || floatParams.Count == 0)
                return;
            
            var arraySize = (floatParams.Count / 4 + (floatParams.Count % 4 == 0 ? 0 : 1)) * 4; // always 16byte slices for alignment
            var array = new float[arraySize];
            
            for (var i = 0; i < floatParams.Count; i++)
            {
                array[i] = floatParams[i];
            }
            
            var device = ResourceManager.Device;

            var size = sizeof(float) * array.Length;
            using (var data = new DataStream(size, true, true))
            {
                data.WriteRange(array);
                data.Position = 0;

                if (floatSlotBuffer.Value == null || floatSlotBuffer.Value.Description.SizeInBytes != size)
                {
                    Utilities.Dispose(ref floatSlotBuffer.Value);
                    var bufferDesc = new BufferDescription
                                         {
                                             Usage = ResourceUsage.Default,
                                             SizeInBytes = size,
                                             BindFlags = BindFlags.ConstantBuffer
                                         };
                    floatSlotBuffer.Value = new Buffer(device, data, bufferDesc);
                }
                else
                {
                    device.ImmediateContext.UpdateSubresource(new DataBox(data.DataPointer, 0, 0), floatSlotBuffer.Value, 0);
                }
            }
            floatSlotBuffer.Value.DebugName = nameof(FloatsToBuffer);
        }
        catch (Exception e)
        {
            Log.Warning("Failed to creat float value buffer" + e.Message);
        }        
    }    
    
        
    #region Implementation of IStatusProvider
    IStatusProvider.StatusLevel IStatusProvider.GetStatusLevel() =>
        string.IsNullOrEmpty(_lastErrorMessage) ? IStatusProvider.StatusLevel.Success : IStatusProvider.StatusLevel.Warning;

    string IStatusProvider.GetStatusMessage() => _lastErrorMessage;
    private string _lastErrorMessage = string.Empty;
    #endregion
    
    private ShaderGraphNode _graphNode;
    private bool _needsInvalidation = true;
    private string _additionalDefines = "";


    [Input(Guid = "FFC1C70E-B717-4337-916D-C3A13343E9CC")]
    public readonly InputSlot<ShaderGraphNode> Field = new();
    
    [Input(Guid = "BCF6DE27-1FFD-422C-9F5B-910D89CAD1A4")]
    public readonly InputSlot<string> TemplateCode = new();

    [Input(Guid = "F6FB3BE8-53F2-4D68-BF0F-3F519BC09FF4")]
    public readonly InputSlot<string> AdditionalDefines = new();

    
    public static string ToHlslTemplateTag(string hook)
    {
        return $"/*{{{hook}}}*/";
    }
}