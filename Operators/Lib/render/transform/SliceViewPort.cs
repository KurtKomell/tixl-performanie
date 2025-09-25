using T3.Core.Utils;

namespace Lib.render.transform;

[Guid("8b888408-e472-4bf9-be25-17a3dd8b90fd")]
internal sealed class SliceViewPort : Instance<SliceViewPort>
{
    [Output(Guid = "ba5c7143-d37b-494f-acd8-1ad139215f63")]
    public readonly Slot<Command> Output = new(new Command());

    [Output(Guid = "4C60CE60-1953-4822-83B8-14589559796E")]
    public readonly Slot<int> Count = new();

    public SliceViewPort()
    {
        Output.UpdateAction += Update;
        Count.UpdateAction += Update;
    }

    private const int MaxCells = 1000;
        
    private void Update(EvaluationContext context)
    {
        Count.DirtyFlag.Clear();
        var stretch = Stretch.GetValue(context);

        var repeatView = Mode.GetValue(context) == 0;
            
        _prevResolution = context.RequestedResolution;
        var cells = CellCounts.GetValue(context);
        cells.Width = Math.Max(cells.Width, 1);
        cells.Height = Math.Max(cells.Height, 1);
        
        
        var cellCount = cells.Width * cells.Height;
        Count.Value = Math.Min(cellCount , MaxCells);
            
        var cellSize = new Vector2(_prevResolution.Width / (float)cells.Width,
                                   _prevResolution.Height / (float)cells.Height);

        var cellIndex = CellIndex.GetValue(context).Clamp(0,int.MaxValue);
        var modCellIndex = cellIndex % cellCount;
        var columnIndex = modCellIndex % cells.Width;
        var rowIndex = modCellIndex / cells.Width;

        var cellSizeX = cellSize.X * stretch.X;
        var cellSizeY = cellSize.Y * stretch.Y;
        
        var newViewPort = new RawViewportF
                              {
                                  X = (columnIndex + (1 - stretch.X) / 2) * cellSize.X,
                                  Y = (rowIndex + (1 - stretch.Y) / 2) * cellSize.Y,
                                  Width = cellSizeX,
                                  Height = cellSizeY,
                                  MinDepth = 0,
                                  MaxDepth = 1
                              };

        var m = context.CameraToClipSpace;
        _prevCameraToClipSpace= m;

        if (repeatView)
        {
            var viewPortStretch = new Vector2(cells.Width/(float)cells.Height,1);
            m.M31 += Vector2.Zero.X;
            m.M32 += Vector2.Zero.Y;
            m.M11 *= viewPortStretch.X / stretch.X;
            m.M22 *= viewPortStretch.Y / stretch.Y;
        }
        else
        {
            m.M31 += MathUtils.Remap(columnIndex + 0.5f, 0,(float)cells.Width, -cells.Width  , cells.Width );
            m.M32 += MathUtils.Remap(rowIndex + 0.5f, 0,(float)cells.Height, cells.Height  , -cells.Height );
            m.M11 *= 1/ stretch.X * cells.Width;
            m.M22 *= 1 /stretch.Y * cells.Height;
        }
        context.CameraToClipSpace = m;
            
        var deviceContext = ResourceManager.Device.ImmediateContext;
        var rasterizer = deviceContext.Rasterizer;            
            
        _prevViewports = rasterizer.GetViewports<RawViewportF>();
        _prevRasterizerState = rasterizer.State;

        context.RequestedResolution = new Int2((int)cellSizeX.Clamp(1,16384),
                                               (int)cellSizeY.Clamp(1,16384));
        rasterizer.SetViewport(newViewPort);

        // Execute subgraph
        SubGraph.GetValue(context);

        rasterizer.SetViewports(_prevViewports, _prevViewports.Length);
        rasterizer.State = _prevRasterizerState;
        context.RequestedResolution = _prevResolution;
        context.CameraToClipSpace = _prevCameraToClipSpace;
    }
        
    private RawViewportF[] _prevViewports;
    private Int2 _prevResolution;

    [Input(Guid = "21532B24-FED2-403B-ABB1-6FAA19311366")]
    public readonly InputSlot<Command> SubGraph = new ();
        
        
    [Input(Guid = "0EC5EDFC-426C-40BB-A68E-80E4CAA4C7BF")]
    public readonly InputSlot<int> CellIndex = new ();
        
    [Input(Guid = "4D4DB9AD-B3A3-4DA6-8716-23D21C5FFC4A")]
    public readonly InputSlot<Int2> CellCounts = new ();

    [Input(Guid = "AA060596-BA04-4862-B123-E328F1EF58E1")]
    public readonly InputSlot<Vector2> Stretch = new ();

    [Input(Guid = "354EB18C-ABA0-43E6-B534-C119176547FF", MappedType = typeof(ViewModes))]
    public readonly InputSlot<int> Mode = new ();
        
    private RasterizerState _prevRasterizerState;
    private Matrix4x4 _prevCameraToClipSpace;

    private enum ViewModes
    {
        RepeatView,
        SliceView,
    }
}