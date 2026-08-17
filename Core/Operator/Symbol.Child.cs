#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using T3.Core.Compilation;
using T3.Core.DataTypes;
using T3.Core.Logging;
using T3.Core.Model;
using T3.Core.Operator.Slots;
using T3.Core.Utils;
using Texture2D = T3.Core.DataTypes.Texture2D;

namespace T3.Core.Operator;

public partial class Symbol
{
    /// <summary>
    /// Represents an instance of a <see cref="Symbol"/> within a Symbol.
    /// </summary>
    public sealed class Child
    {
        /// <summary>A reference to the <see cref="Symbol"/> this is an instance from.</summary>
        public Symbol Symbol { get; }

        public Guid Id { get; }

        public Symbol? Parent { get; }

        public string Name { get; set; }

        public string ReadableName => string.IsNullOrEmpty(Name) ? Symbol.Name : Name;
        public bool HasCustomName => !string.IsNullOrEmpty(Name);

        public bool IsBypassed { get => _isBypassed; set => SetBypassed(value); }

        public bool IsDisabled
        {
            get
            {
                // Avoid LINQ because of allocations in inner loop
                foreach (var x in Outputs.Values)
                {
                    if (x.IsDisabled)
                        return true;
                }

                return false;
                //return Outputs.FirstOrDefault().Value?.IsDisabled ?? false;
            }
            set => SetDisabled(value);
        }

        public Dictionary<Guid, Input> Inputs { get; private init; } = new();
        public Dictionary<Guid, Output> Outputs { get; private init; } = new(); 
        public IEnumerable<Instance> Instances
        {
            get
            {
                lock(_creationLock)
                    return _instancesOfSelf.Values;
            }
        }

        private readonly Dictionary<int, Instance> _instancesOfSelf = [];
        private readonly object _creationLock;
        // ReSharper disable once NotAccessedField.Local
        private readonly bool _isGeneric;

        public Guid? PreviousId { get; private set; }


        internal Child(Symbol symbol, Guid childId, Symbol? parent, string? name, bool isBypassed, object creationLock, Guid? previousId = null)
        {
            _creationLock = creationLock;
            Symbol = symbol;
            Id = childId;
            Parent = parent;
            Name = name ?? string.Empty;
            _isBypassed = isBypassed;
            _isGeneric = symbol.IsGeneric;
            PreviousId = previousId;

            foreach (var inputDefinition in symbol.InputDefinitions)
            {
                if (!Inputs.TryAdd(inputDefinition.Id, new Input(inputDefinition)))
                {
                    throw new ApplicationException($"The ID for symbol input {symbol.Name}.{inputDefinition.Name} must be unique.");
                }
            }

            foreach (var outputDefinition in symbol.OutputDefinitions)
            {
                Symbol.OutputDefinition.TryGetNewOutputDataType(outputDefinition, out var outputData);
                var output = new Output(outputDefinition, outputData) { DirtyFlagTrigger = outputDefinition.DirtyFlagTrigger };
                if (!Outputs.TryAdd(outputDefinition.Id, output))
                {
                    throw new ApplicationException($"The ID for symbol output {symbol.Name}.{outputDefinition.Name} must be unique.");
                }
            }
        }

        private void SetDisabled(bool shouldBeDisabled)
        {
            if (Parent == null)
                return;

            var outputDefinitions = Symbol.OutputDefinitions;

            // Set disabled status on this child's outputs
            foreach (var outputDef in outputDefinitions)
            {
                if (outputDef == null)
                {
                    Log.Warning($"{Symbol.GetType()} {Symbol.Name} contains a null {typeof(Symbol.OutputDefinition)}", Id);
                    continue;
                }

                if (Outputs.TryGetValue(outputDef.Id, out var childOutput))
                {
                    childOutput.IsDisabled = shouldBeDisabled;

                }
                else
                {
                    Log.Warning($"{typeof(Symbol.Child)} {ReadableName} does not have the following child output as defined: " +
                                $"{childOutput?.OutputDefinition.Name}({nameof(Guid)}{childOutput?.OutputDefinition.Id})");
                }
            }

            // Set disabled status on outputs of each instanced copy of this child within all parents that contain it
            foreach (var parentInstance in Parent.InstancesOfSelf)
            {
                // This parent doesn't have an instance of our SymbolChild. Ignoring and continuing.
                if (!parentInstance.Children.TryGetChildInstance(Id, out var matchingChildInstance))
                    continue;

                // Set disabled status on all outputs of each instance
                foreach (var slot in matchingChildInstance.Outputs)
                {
                    slot.IsDisabled = shouldBeDisabled;
                }
            }
        }

        #region sub classes =============================================================
        public sealed class Output
        {
            public Symbol.OutputDefinition OutputDefinition { get; }
            public IOutputData OutputData { get; }

            public bool IsDisabled { get; set; }

            public DirtyFlagTrigger DirtyFlagTrigger
            {
                get => _dirtyFlagTrigger ?? OutputDefinition.DirtyFlagTrigger;
                set => _dirtyFlagTrigger = (value != OutputDefinition.DirtyFlagTrigger) ? (DirtyFlagTrigger?)value : null;
            }

            private DirtyFlagTrigger? _dirtyFlagTrigger = null;

            internal Output(Symbol.OutputDefinition outputDefinition, IOutputData outputData)
            {
                OutputDefinition = outputDefinition;
                OutputData = outputData;
            }

            public Output DeepCopy()
            {
                return new Output(OutputDefinition, OutputData);
            }
        }

        public sealed class Input
        {
            public InputDefinition InputDefinition { get; }
            public Guid Id => InputDefinition.Id;
            public bool IsMultiInput => InputDefinition.IsMultiInput;
            public InputValue DefaultValue => InputDefinition.DefaultValue;

            public string Name => InputDefinition.Name;

            /// <summary>The input value used for this symbol child</summary>
            public InputValue Value { get; }

            public bool IsDefault { get; set; }

            public Input(Symbol.InputDefinition inputDefinition)
            {
                InputDefinition = inputDefinition;
                Value = DefaultValue.Clone();
                IsDefault = true;
            }

            public void SetCurrentValueAsDefault()
            {
                if (DefaultValue.IsEditableInputReferenceType)
                {
                    DefaultValue.AssignClone(Value);
                }
                else
                {
                    DefaultValue.Assign(Value);
                }

                IsDefault = true;
            }

            public void ResetToDefault()
            {
                if (DefaultValue.IsEditableInputReferenceType)
                {
                    Value.AssignClone(DefaultValue);
                }
                else
                {
                    Value.Assign(DefaultValue);
                }

                IsDefault = true;
            }
        }
        #endregion
        
        #region Bypass

        private bool _isBypassed;

        public bool IsBypassable()
        {
            if (Symbol.OutputDefinitions.Count == 0)
                return false;

            if (Symbol.InputDefinitions.Count == 0)
                return false;

            var mainInput = Symbol.InputDefinitions[0];
            var mainOutput = Symbol.OutputDefinitions[0];

            var defaultValueType = mainInput.DefaultValue.ValueType;
            if (defaultValueType != mainOutput.ValueType)
                return false;

            return _bypassableTypes.Contains(defaultValueType);
        }
        
        private static readonly Type[] _bypassableTypes =
        {
            typeof(Command),
            typeof(Texture2D),
            typeof(BufferWithViews),
            typeof(MeshBuffers),
            typeof(float),
            typeof(Vector2),
            typeof(Vector3),
            typeof(string),
            typeof(ShaderGraphNode)
        };

        private void SetBypassed(bool shouldBypass)
        {
            if (shouldBypass == _isBypassed)
                return;

            if (!IsBypassable())
                return;

            if (Parent == null)
            {
                // Clarify: shouldn't this be shouldBypass?
                _isBypassed = shouldBypass; // during loading parents are not yet assigned. This flag will later be used when creating instances
                return;
            }

            lock (_creationLock)
            {
                if (_instancesOfSelf.Count == 0)
                {
                    _isBypassed = shouldBypass; // while duplicating / cloning as new symbol there are no instances yet.
                    return;
                }
            }

            // check if there is a connection
            var isOutputConnected = false;
            var mainOutputDef = Symbol.OutputDefinitions[0];
            foreach (var connection in Parent.Connections)
            {
                if (connection.SourceSlotId != mainOutputDef.Id || connection.SourceParentOrChildId != Id)
                    continue;

                isOutputConnected = true;
                break;
            }

            if (!isOutputConnected)
                return;

            var id = Id;
            foreach (var parentInstance in Parent.InstancesOfSelf)
            {
                var instance = parentInstance.Children[id];
                Instance.SetBypassFor(instance, shouldBypass);
            }

            _isBypassed = shouldBypass;
        }
        
        #endregion Bypass

        public override string ToString()
        {
            return Parent?.Name + ">" + ReadableName;
        }

        internal static unsafe Guid CreateIdDeterministically(Symbol symbol, Symbol? parent, Guid? extra = null)
        {
            //deterministically create a new guid from the symbol id
            using var hashComputer = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
            var symbolId = symbol.Id;
            var symbolIdBytes = new ReadOnlySpan<byte>(&symbolId, 16);
            hashComputer.AppendData(symbolIdBytes);

            if (parent != null)
            {
                var parentId = parent.Id;
                var parentIdBytes = new ReadOnlySpan<byte>(&parentId, 16);
                hashComputer.AppendData(parentIdBytes);
            }

            if (extra != null)
            {
                var val = extra.Value;
                var bytes = new ReadOnlySpan<byte>(&val, 16);
                hashComputer.AppendData(bytes);
            }

            // SHA1 is 20 bytes long, but we only need 16 bytes for a guid
            var newGuidBytes = new ReadOnlySpan<byte>(hashComputer.GetHashAndReset(), 0, 16);
            return new Guid(newGuidBytes);
        }

        internal void RemoveChildInstancesOf(Child child)
        {
            var idToDestroy = child.Id;
            lock (_creationLock)
            {
                foreach (var instanceKvp in _instancesOfSelf)
                {
                    var instance = instanceKvp.Value;
                    if (instance.Children.TryGetChildInstance(idToDestroy, out var childInstance, false))
                    {
                        childInstance.Dispose(null);
                    }
                }
            }
        }

        public void DestroyAndClearAllInstances(SymbolPackage? onlyDisposeInPackage)
        {
            lock (_creationLock)
            {
                var allInstances = _instancesOfSelf.Values.ToArray();
                for (int i = allInstances.Length - 1; i >= 0; i--)
                {
                    allInstances[i].Dispose(onlyDisposeInPackage); // removes self from _instancesOfSelf dict
                }
                
                Debug.Assert(_instancesOfSelf.Count == 0, $"All instances of {Symbol.Name} should have been disposed, but {_instancesOfSelf.Count} remain.");
            }
        }

        public void Dispose()
        {
            DestroyAndClearAllInstances(null);
            lock (_creationLock)
            {
                var removed = Symbol._childrenCreatedFromMe.Remove(Id, out _);
                //Debug.Assert(removed);
            }
        }

        internal void AddChildInstances(Child newChild, ICollection<Instance> listToAddNewInstancesTo)
        {
            lock (_creationLock)
            {
                foreach (var instance in _instancesOfSelf.Values)
                {
                    var path = instance.InstancePath.Append(newChild.Id).ToArray();
                    if (newChild.TryGetOrCreateInstance(path, out var newInstance, out var created, true))
                    {
                        if (created)
                        {
                            listToAddNewInstancesTo.Add(newInstance);
                        }
                    }
                }
            }
        }

        internal bool UpdateIOAndConnections(SlotChangeInfo slotChanges)
        {
            UpdateSymbolChildIO(this, slotChanges);

            if (Parent == null)
            { 
                DestroyAndClearAllInstances(Symbol.SymbolPackage);
                // just destroy all instances - we have no connections to worry about since we dont have a parent
                return false;
            }

            // we dont need to update our instances/connections - our parents do that for us if they need it
            if (Parent.NeedsTypeUpdate && Parent.SymbolPackage == Symbol.SymbolPackage)
            {
                // destroy all instances if necessary? probably not...
                //DestroyAndClearAllInstances();
                return false;
            }

            // deal with removed connections
            var parentConnections = Parent!.Connections;
            // get all connections that belong to this instance
            var connectionsToReplace = parentConnections.FindAll(c => c.SourceParentOrChildId == Id ||
                                                                      c.TargetParentOrChildId == Id);

            // first remove those connections where the inputs/outputs doesn't exist anymore
            var connectionsToRemove =
                connectionsToReplace.FindAll(c =>
                                             {
                                                 return slotChanges.RemovedOutputDefinitions.Any(output =>
                                                                                                 {
                                                                                                     var outputId = output.Id;
                                                                                                     return outputId == c.SourceSlotId ||
                                                                                                         outputId == c.TargetSlotId;
                                                                                                 })
                                                        || slotChanges.RemovedInputDefinitions.Any(input =>
                                                                                                   {
                                                                                                       var inputId = input.Id;
                                                                                                       return inputId == c.SourceSlotId ||
                                                                                                           inputId == c.TargetSlotId;
                                                                                                   });
                                             });

            foreach (var connection in connectionsToRemove)
            {
                Parent.RemoveConnection(connection); // TODO: clarify if we need to iterate over all multi input indices
                connectionsToReplace.Remove(connection);
            }

            // now create the entries for those that will be reconnected after the instance has been replaced. Take care of the multi input order
            //connectionsToReplace.Reverse();

            foreach (var con in connectionsToReplace)
            {
                Parent.ReplaceConnection(con);
            }
            
            return false;
        }

        private bool TryCreateNewInstance(Instance? parentInstance,
                                           [NotNullWhen(true)] out Instance? newInstance,
                                           bool initialize = true)
        {
            var path = parentInstance == null ? new[] { Id } : parentInstance.InstancePath.Append(Id).ToArray();
            var parent = parentInstance?.SymbolChild;
            var pathHash = HashCodeOf(path);

            // Register under the lock only. Wire + Initialize run outside so nested graph work
            // cannot re-enter _creationLock mid-construct (caused "already has a symbol child").
            var alreadyExisted = false;
            lock (_creationLock)
            {
                if (_instancesOfSelf.TryGetValue(pathHash, out newInstance))
                {
                    // Drop corrupt half-bound entries left by a failed SetSymbolInfo/Wire.
                    if (newInstance.SymbolChild == null)
                    {
                        _instancesOfSelf.Remove(pathHash);
                        newInstance = null;
                    }
                    else
                    {
                        alreadyExisted = true;
                    }
                }

                if (!alreadyExisted && !TryInstantiateAndRegister(parent, parentInstance, path, pathHash, out newInstance, out var reason))
                {
                    Log.Error(reason);
                    return false;
                }
            }

            if (!alreadyExisted)
            {
                if (newInstance == null)
                    return false;

                try
                {
                    WireInstanceSlots(newInstance);
                }
                catch (Exception e)
                {
                    lock (_creationLock)
                    {
                        _instancesOfSelf.Remove(pathHash);
                    }

                    Log.Error($"Failed to wire instance of type {Symbol.InstanceType} with id {Id}: {e}");
                    newInstance = null;
                    return false;
                }
            }

            if (newInstance == null)
                return false;

            if (!initialize)
                return true;

            if (alreadyExisted && (newInstance.Initialized || newInstance.IsReconnecting))
                return true;

            try
            {
                if (!newInstance.Initialized)
                    newInstance.Initialize(parentInstance);
            }
            catch (Exception e)
            {
                if (!alreadyExisted)
                {
                    lock (_creationLock)
                    {
                        _instancesOfSelf.Remove(pathHash);
                    }
                }

                Log.Error($"Failed to initialize instance of type {Symbol.InstanceType} with id {Id}: {e}");
                Console.Error.WriteLine($"[T3] Failed to initialize {Symbol.Name} ({Symbol.InstanceType}): {e}");
                newInstance = null;
                return false;
            }

            return true;
        }

        private bool TryInstantiateAndRegister(Symbol.Child? parentSymbolChild, Instance? parentInst, Guid[] newInstancePath, int pathHash,
                                   [NotNullWhen(true)] out Instance? newInstance,
                                   [NotNullWhen(false)] out string? reason2)
        {
            if(parentSymbolChild != null)
            {
                if(parentSymbolChild.Symbol != Parent)
                {
                    throw new InvalidOperationException($"Parent symbol {parentSymbolChild.Symbol} does not match {Symbol}");
                }
                    
                if (newInstancePath[^2] != parentSymbolChild.Id)
                {
                    throw new InvalidOperationException($"Instance path does not match parent id {parentSymbolChild.Id}");
                }
            }
            else
            {
                if(Parent != null)
                    throw new InvalidOperationException("symbol child has no parent but parent instance provided is not null");
            }

            if (!TryInstantiate(out newInstance, out reason2))
            {
                Log.Error(reason2);
                return false;
            }

            if (newInstance.SymbolChild != null)
            {
                reason2 = $"Instantiate of {Symbol.InstanceType} returned an instance that already has SymbolChild {newInstance.SymbolChild}";
                Log.Error(reason2);
                newInstance = null;
                return false;
            }
                
            if (!_instancesOfSelf.TryAdd(pathHash, newInstance))
            {
                // Another re-entrant creator won — use that instance instead of throwing.
                if (_instancesOfSelf.TryGetValue(pathHash, out var existing) && existing.SymbolChild != null)
                {
                    newInstance = existing;
                    reason2 = null;
                    return true;
                }

                // Stale half-bound entry — remove and fall through to bind our new instance.
                _instancesOfSelf.Remove(pathHash);
                if (!_instancesOfSelf.TryAdd(pathHash, newInstance))
                {
                    reason2 = "Attempted to create a new instance when one already exists at that path";
                    newInstance = null;
                    return false;
                }
            }

            try
            {
                newInstance.SetSymbolInfo(this, parentSymbolChild, newInstancePath, pathHash);
            }
            catch (Exception e)
            {
                _instancesOfSelf.Remove(pathHash);
                reason2 = $"Failed to bind symbol info for {Symbol.InstanceType} with id {Id}: {e}";
                Log.Error(reason2);
                newInstance = null;
                return false;
            }

            return true;
        }

        private void WireInstanceSlots(Instance newInstance)
        {
            if (newInstance == null)
                throw new ArgumentNullException(nameof(newInstance));

            // cache property accesses for performance
            var newInstanceInputDefinitions = Symbol.InputDefinitions;
            if (newInstanceInputDefinitions == null)
                throw new InvalidOperationException($"InputDefinitions null for {Symbol}");

            var newInstanceInputDefinitionCount = newInstanceInputDefinitions.Count;

            var newInstanceInputs = newInstance.Inputs
                                    ?? throw new InvalidOperationException($"Inputs null on fresh instance of {Symbol.InstanceType}");
            var newInstanceInputCount = newInstanceInputs.Count;

            var symbolChildInputs = Inputs
                                    ?? throw new InvalidOperationException($"Child Inputs null for {this}");

            // set up the inputs for the child instance
            for (int i = 0; i < newInstanceInputDefinitionCount; i++)
            {
                if (i >= newInstanceInputCount)
                {
                    Log.Warning($"Skipping undefined input index");
                    continue;
                }

                var inputDefinitionId = newInstanceInputDefinitions[i].Id;
                var inputSlot = newInstanceInputs[i];
                if (inputSlot == null)
                {
                    Log.Warning($"Skipping null input slot at index {i}");
                    continue;
                }

                if (!symbolChildInputs.TryGetValue(inputDefinitionId, out var input) || input == null)
                {
                    Log.Warning($"Skipping undefined input: {inputDefinitionId}");
                    continue;
                }

                inputSlot.Input = input;
                inputSlot.Id = inputDefinitionId;
            }

            // cache property accesses for performance
            var childOutputDefinitions = Symbol.OutputDefinitions
                                         ?? throw new InvalidOperationException($"OutputDefinitions null for {Symbol}");
            var childOutputDefinitionCount = childOutputDefinitions.Count;

            var childOutputs = newInstance.Outputs
                               ?? throw new InvalidOperationException($"Outputs null on fresh instance of {Symbol.InstanceType}");

            var symbolChildOutputs = Outputs
                                     ?? throw new InvalidOperationException($"Child Outputs null for {this}");

            // set up the outputs for the child instance
            for (int i = 0; i < childOutputDefinitionCount; i++)
            {
                Debug.Assert(i < childOutputs.Count);
                var outputDefinition = childOutputDefinitions[i];
                var id = outputDefinition.Id;
                if (i >= childOutputs.Count)
                {
                    Log.Warning($"Skipping undefined output: {id}");
                    continue;
                }

                var outputSlot = childOutputs[i];
                outputSlot.Id = id;
                var symbolChildOutput = symbolChildOutputs[id];
                if (outputDefinition.OutputDataType != null)
                {
                    // output is using data, so link it
                    if (outputSlot is IOutputDataUser outputDataConsumer)
                    {
                        outputDataConsumer.SetOutputData(symbolChildOutput.OutputData);
                    }
                }

                outputSlot.DirtyFlag.Trigger = symbolChildOutput.DirtyFlagTrigger;
                outputSlot.IsDisabled = symbolChildOutput.IsDisabled;
            }
        }

        private bool TryInstantiate([NotNullWhen(true)] out Instance? instance,
                                    [NotNullWhen(false)] out string? reason3)
        {
            if (Symbol.SymbolPackage.AssemblyInformation.OperatorTypeInfo.TryGetValue(Symbol.Id, out var typeInfo))
            {
                var constructor = typeInfo.GetConstructor();
                try
                {
                    instance = (Instance)constructor.Invoke();
                    reason3 = string.Empty;
                    return true;
                }
                catch (Exception e)
                {
                    reason3 = $"Failed to create instance of type {Symbol.InstanceType} with id {Id}: {e}";
                    instance = null;
                    return false;
                }
            }

            Log.Error($"No constructor found for {Symbol.InstanceType}. This should never happen!! Please report this");

            try
            {
                // create instance through reflection
                instance = Activator.CreateInstance(Symbol.InstanceType,
                                                    AssemblyInformation.ConstructorBindingFlags,
                                                    binder: null,
                                                    args: Array.Empty<object>(),
                                                    culture: null) as Instance;

                if (instance is null)
                {
                    reason3 = $"(Instance creation fallback failure) Failed to create instance of type " +
                              $"{Symbol.InstanceType} with id {Id} - result was null";
                    return false;
                }

                Log.Warning($"(Instance creation fallback) Created instance of type {Symbol.InstanceType} with id {Id} through reflection");

                reason3 = string.Empty;
                return true;
            }
            catch (Exception e)
            {
                reason3 = $"(Instance creation fallback failure) Failed to create instance of type {Symbol.InstanceType} with id {Id}: {e}";
                instance = null;
                return false;
            }
        }

        internal void AddConnectionToInstances(Connection connection, int multiInputIndex, bool allowCreate)
        {
            lock (_creationLock)
            {
                foreach (var instance in _instancesOfSelf.Values)
                {
                    instance.TryAddConnection(connection, multiInputIndex, allowCreate);
                }
            }
        }

        internal void RemoveConnectionFromInstances(Connection connection, int multiInputIndex)
        {
            lock (_creationLock)
            {
                foreach (var instance in _instancesOfSelf.Values)
                {
                    if (instance.TryGetTargetSlot(connection, out var targetSlot, false))
                    {
                        targetSlot.RemoveConnection(multiInputIndex);
                    }
                }
            }
        }

        internal void InvalidateInputDefaultInInstances(in Guid inputId)
        {
            lock (_creationLock)
            {
                foreach (var instance in _instancesOfSelf.Values)
                {
                    var inputSlots = instance.Inputs;
                    for (int i = 0; i < inputSlots.Count; i++)
                    {
                        var slot = inputSlots[i];
                        if (slot.Id != inputId)
                            continue;

                        if (!slot.Input.IsDefault)
                            continue;

                        slot.DirtyFlag.Invalidate();
                        break;
                    }
                }
            }
        }

        internal void InvalidateInputInChildren(in Guid inputId, in Guid childId)
        {
            lock (_creationLock)
            {
                foreach (var instanceInfo in _instancesOfSelf)
                {
                    var instance = instanceInfo.Value;
                    
                    //var child = instance.Children[childId];
                    if (!instance.Children.TryGetChildInstance(childId, out var child))
                    {
                        Log.Debug("Failed to invalidate missing child");
                        continue;
                    }

                    var inputSlots = child.Inputs;
                    for (int j = 0; j < inputSlots.Count; j++)
                    {
                        var slot = inputSlots[j];
                        if (slot.Id != inputId)
                            continue;

                        slot.DirtyFlag.Invalidate();
                        break;
                    }
                }
            }
        }

        internal void SortInputSlotsByDefinitionOrder()
        {
            lock (_creationLock)
            {
                foreach (var instance in _instancesOfSelf.Values)
                {
                    Instance.SortInputSlotsByDefinitionOrder(instance);
                }
            }
        }

        internal void RemoveDisposedInstance(Instance child, int hash)
        {
            lock (_creationLock)
            {
                if (!_instancesOfSelf.Remove(hash))
                {
                    Log.Error($"Could not find instance {child} to remove from {this}");
                }
            }
        }

        internal void PrepareForReload()
        {
            DestroyAndClearAllInstances(Symbol.SymbolPackage);
        }

        public bool TryGetOrCreateInstance(IReadOnlyList<Guid> path, [NotNullWhen(true)] out Instance? instance, out bool created, bool allowCreate = true, bool initialize = true)
        {
            // throw exceptions if the path is invalid
            if (path.Count == 0)
            {
                throw new ArgumentException("Path must not be empty");
            }
            
            if(!path[^1].Equals(Id))
            {
                throw new ArgumentException($"Path must end with {Id}");
            }

            if (Parent == null)
            {
                if(path.Count != 1)
                    throw new ArgumentException("Path must be of length 1 if parent is null");
                
                if(path[0] != Id)
                    throw new ArgumentException($"Path must be {Id} if parent is null");
            }
            else if (path.Count < 2)
            {
                throw new ArgumentException("Path must be of length 2 or more if parent is not null");
            }
            
            var hash = HashCodeOf(path);
            Instance? existing = null;
            var needsReconnect = false;

            lock (_creationLock)
            {
                if (_instancesOfSelf.TryGetValue(hash, out existing))
                {
                    // Defer ReconnectChildren until after the lock is released.
                    needsReconnect = allowCreate && existing.NeedsInternalReconnections;
                    instance = existing;
                    created = false;
                }
                else if (!allowCreate)
                {
                    created = false;
                    instance = null;
                    return false;
                }
                else
                {
                    instance = null;
                    created = false;
                }
            }

            if (existing != null)
            {
                // When initialize:false, the BFS ReconnectChildren owns the tree walk.
                // Also skip when the parent is mid-reconnect: CreateConnections/Values used to
                // re-enter ReconnectChildren per child and recreate deep recursion (AV / init failure).
                if (needsReconnect && initialize)
                {
                    // Never nest ReconnectChildren while any ancestor is mid-BFS.
                    var ancestorBusy = false;
                    if (existing.TryGetParentInstance(out var walk, allowCreate: false))
                    {
                        for (var p = walk; p != null; )
                        {
                            if (p.IsReconnecting)
                            {
                                ancestorBusy = true;
                                break;
                            }

                            if (!p.TryGetParentInstance(out var next, allowCreate: false))
                                break;
                            p = next;
                        }
                    }

                    if (!ancestorBusy)
                    {
                        if (existing.TryGetParentInstance(out var parent, allowCreate: false)
                            && parent is { NeedsInternalReconnections: true, Initialized: true })
                        {
                            parent.ReconnectChildren();
                        }
                        else if (existing.NeedsInternalReconnections)
                        {
                            existing.ReconnectChildren();
                        }
                    }
                }

                instance = existing;
                created = false;
                return true;
            }

            // Create outside the lookup lock — TryCreateNewInstance manages its own lock
            // only around dictionary registration, not around Initialize/ReconnectChildren.
            if (Parent == null)
            {
                created = TryCreateNewInstance(null, out instance, initialize);
                return created;
            }

            if (TryGetParentInstanceLocal(path, initialize, out created, out var parentInstance))
            {
                // Hash lookup above already established we are not registered yet.
                // Do not call TryGetChildInstance again (re-enters with a mutable path buffer).
                created = TryCreateNewInstance(parentInstance, out instance, initialize);
                return created;
            }

            created = false;
            instance = null;
            return false;

            bool GetParentAsChild(IReadOnlyList<Guid> readOnlyList, [NotNullWhen(true)] out Child? child)
            {
                if (Parent == null)
                {
                    child = null;
                    return false;
                }

                var parentSymbolChildId = readOnlyList[^2];
                return Parent.ChildrenCreatedFromMe.TryGetValue(parentSymbolChildId, out child);
            }

            bool TryGetParentInstanceLocal(IReadOnlyList<Guid> guids, bool init, out bool wasCreated, [NotNullWhen(true)] out Instance? parentInstance)
            {
                if (!GetParentAsChild(guids, out var parentSymbolChild))
                {
                    parentInstance = null;
                    wasCreated = false;
                    return false;
                }

                var parentPath = guids.SkipLast(1).ToArray();
                var gotParent = parentSymbolChild.TryGetOrCreateInstance(parentPath, out parentInstance, out wasCreated, allowCreate: true, initialize: init);
                return gotParent;
            }
        }

        public static int HashCodeOf(IReadOnlyList<Guid> path)
        {
            if (path == null)
                throw new ArgumentNullException(nameof(path), "HashCodeOf received a null path — likely memory corruption upstream.");

            var count = path.Count;
            if (count == 0)
                throw new ArgumentException("HashCodeOf received an empty path", nameof(path));

            int hash = path[0].GetHashCode();
            for (int i = 1; i < count; i++)
            {
                hash = HashCode.Combine(hash, path[i].GetHashCode());
            }
            return hash;
        }

        public void ClearPreviousId()
        {
            PreviousId = null;
        }

  

        public bool SearchForChild(Guid search, [NotNullWhen(true)] out Child? child, [NotNullWhen(true)] out IReadOnlyList<Guid>? path)
        {
            return SearchForChild(search, ReadOnlySpan<Guid>.Empty, out child, out path);
        }

        private bool SearchForChild(Guid search, ReadOnlySpan<Guid> path, out Child? child, [NotNullWhen(true)] out IReadOnlyList<Guid>? fullPath)
        {
            Span<Guid> pathIncludingMe = stackalloc Guid[path.Length + 1];
            path.CopyTo(pathIncludingMe);
            pathIncludingMe[^1] = Id;
            if (Id == search)
            {
                child = this;
                fullPath = pathIncludingMe.ToArray();
                return true;
            }

            var symbol = Symbol;
            foreach (var symbolChild in symbol.Children.Values)
            {
                if(symbolChild.SearchForChild(search, pathIncludingMe, out var foundChild, out var foundPath))
                {
                    child = foundChild;
                    fullPath = foundPath;
                    return true;
                }
            }
            
            child = null;
            fullPath = null;
            return false;
        }

        internal void ReconnectAllChildren()
        {
            lock (_creationLock)
            {
                foreach (var inst in _instancesOfSelf.Values)
                {
                    inst.ReconnectChildren();
                }
            }
        }

        internal void DestroyAllInstances()
        {
            lock (_creationLock)
            {
                // toArray as a defensive copy - these instances will be removed from the dictionary as a result of calling this func
                foreach(var instance in _instancesOfSelf.Values.ToArray())
                {
                    instance.Dispose(null);
                }
            }
        }
    }
}