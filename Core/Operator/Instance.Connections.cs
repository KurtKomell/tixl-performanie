using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using T3.Core.DataTypes;
using T3.Core.Logging;
using T3.Core.Operator.Slots;
using T3.Core.Utils;

namespace T3.Core.Operator;

public abstract partial class Instance
{
    internal void ReconnectChildren()
    {
        if (!NeedsInternalReconnections)
            return;

        // Walk up iteratively — do not recurse into parent.ReconnectChildren()
        // (that reintroduced deep stacks on USB-display-only startups).
        var root = this;
        while (root.TryGetParentInstance(out var parent, false) && parent is { NeedsInternalReconnections: true })
        {
            root = parent;
        }

        if (!ReferenceEquals(root, this))
        {
            root.ReconnectChildren();
            return;
        }

        // Breadth-first: create/wire descendants without nested Initialize/ReconnectChildren.
        // Deep recursive graph load overflowed the stack on some display adapters
        // (reported as AccessViolationException during InstanceChildren construction).
        var queue = new Queue<Instance>();
        queue.Enqueue(this);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!current.NeedsInternalReconnections)
                continue;

            // Deferred instances skip Instance.Initialize; finish the same setup here.
            if (!current.Initialized)
            {
                SortInputSlotsByDefinitionOrder(current);
                current._status |= InstanceStatus.ResourceFoldersDirty;
            }

            // prevent re-entry by setting the connection status prematurely
            current._status |= InstanceStatus.ConnectedInternally;
            current._status |= InstanceStatus.IsReconnecting;

            foreach (var childInst in current.Children.PreExistingValues)
            {
                childInst.DisconnectInputs();
            }

            foreach (var child in current.SymbolChild.Symbol.Children.Values)
            {
                try
                {
                    // Create children without Initialize so nesting depth stays O(1) per level.
                    if (current.Children.TryGetChildInstance(child.Id, out var childInst, allowCreate: true, initialize: false))
                    {
                        if (childInst.NeedsInternalReconnections)
                            queue.Enqueue(childInst);
                    }
                    else
                    {
                        Log.Error($"Failed to create/locate child instance {child.Id} in {current.SymbolChild}");
                    }
                }
                catch (Exception e)
                {
                    // Isolate faults to a single operator instead of aborting the whole graph load.
                    // Also surfaces which operator was involved — the outer catch in TryCreateNewInstance
                    // only knows the top-level project type, not the specific child that failed.
                    var msg = $"Exception creating child {child.Id} ({child.Symbol?.Name}) in {current.SymbolChild}: {e}";
                    Log.Error(msg);
                    Console.Error.WriteLine($"[T3] {msg}");
                }
            }

            try
            {
                CreateConnectionsForInstance(current);
            }
            catch (Exception e)
            {
                var msg = $"Exception connecting children of {current.SymbolChild}: {e}";
                Log.Error(msg);
                Console.Error.WriteLine($"[T3] {msg}");
            }

            current._status &= ~InstanceStatus.IsReconnecting;

            if (!current.Initialized)
            {
                current._status |= InstanceStatus.Initialized;
                current._status |= InstanceStatus.Active;
            }
        }

        return;
        
        static void CreateConnectionsForInstance(Instance instance)
        {
            // create connections between child instances populated with CreateAndAddNewChildInstance
            var child = instance.SymbolChild;
            var symbol = instance.Symbol;
            var connections = symbol.Connections;
        
            // if connections already exist for the symbol, remove any that shouldn't exist anymore
            if (connections.Count != 0)
            {
                var conHashToCount = new Dictionary<ulong, int>(connections.Count);
                for (var index = 0; index < connections.Count; index++) // warning: the order in which these are processed matters
                {
                    var connection = connections[index];
                    ulong highPart = 0xFFFFFFFF & (ulong)connection.TargetSlotId.GetHashCode();
                    ulong lowPart = 0xFFFFFFFF & (ulong)connection.TargetParentOrChildId.GetHashCode();
                    ulong hash = (highPart << 32) | lowPart;
                    conHashToCount.TryGetValue(hash, out int count);

                    // Children were just created above; never allowCreate here — that would
                    // nest Initialize/ReconnectChildren and blow the stack again.
                    if (!instance.TryAddConnection(connection, count, allowCreate: false))
                    {
                        Log.Warning($"Removing obsolete connecting in {symbol}...");
                        // todo: this removal should be moved into the Symbol class
                        connections.RemoveAt(index);
                        index--;
                        continue;
                    }

                    conHashToCount[hash] = count + 1;
                }
            }

            // connect animations if available
            // Children were already created by ReconnectChildren (BFS); do not use Values —
            // that path defaults to initialize:true and would nest ReconnectChildren again.
            symbol.Animator.CreateUpdateActionsForExistingCurves(instance.Children.PreExistingValues);

            if (child.IsBypassed)
            {
                SetBypassFor(instance, true, invalidate: false);
            }
        }
    }

    // disconnects all inputs of this instance - all considered "external" connections
    private int DisconnectInputs()
    {
        // clear our connections - we may be reassigned to another parent
        int disconnectCount = 0;
        for (var index = 0; index < _inputs.Count; index++)
        {
            var input = _inputs[index];
            while (input.HasInputConnections)
            {
                input.RemoveConnection();
                ++disconnectCount;
            }
        }

        return disconnectCount;
    }

    internal bool TryGetTargetSlot(Symbol.Connection connection, [NotNullWhen(true)] out ISlot targetSlot, bool allowCreate)
    {
        // Get target Instance
        var targetParentOrChildId = connection.TargetParentOrChildId;
        IEnumerable<ISlot> targetSlotList;

        if (targetParentOrChildId == Guid.Empty)
        {
            targetSlotList = Outputs;
        }
        else
        {
            if (!Children.TryGetChildInstance(targetParentOrChildId, out var targetInstance, allowCreate, initialize: !IsReconnecting))
            {
                targetSlot = null;
                return false;
            }

            targetSlotList = targetInstance.Inputs;
        }

        foreach (var slot in targetSlotList)
        {
            if (slot.Id != connection.TargetSlotId)
                continue;

            targetSlot = slot;
            return true;
        }

        targetSlot = null;
        return false;
    }

    internal static void SortInputSlotsByDefinitionOrder(Instance instance)
    {
        // order the inputs by the given input definitions. original order is coming from code, but input def order is the relevant one
        var inputs = instance._inputs;
        var inputDefinitions = instance.Symbol.InputDefinitions;
        int numInputs = inputs.Count;
        var lastIndex = numInputs - 1;

        for (int i = 0; i < lastIndex; i++)
        {
            Guid inputId = inputDefinitions[i].Id;
            if (inputs[i].Id != inputId)
            {
                int index = inputs.FindIndex(i + 1, input => input.Id == inputId);
                if (index == -1)
                    continue;
                //Debug.Assert(index >= 0);
                inputs.Swap(i, index);
                Debug.Assert(inputId == inputs[i].Id);
            }
        }

        #if DEBUG
        if (numInputs > 0)
        {
            #if SKIP_ASSERTS
                Debug.Assert(inputs.Count == inputDefinitions.Count);
            #endif
        }
        #endif
    }

    private bool TryGetSourceSlot(Symbol.Connection connection, [NotNullWhen(true)] out ISlot sourceSlot, bool allowCreate)
    {
        // Get source Instance
        IEnumerable<ISlot> sourceSlotList;

        var sourceParentOrChildId = connection.SourceParentOrChildId;
        if (sourceParentOrChildId == Guid.Empty)
        {
            sourceSlotList = Inputs;
        }
        else
        {
            if (!Children.TryGetChildInstance(sourceParentOrChildId, out var sourceInstance, allowCreate, initialize: !IsReconnecting))
            {
                sourceSlot = null;
                return false;
            }

            sourceSlotList = sourceInstance.Outputs;
        }

        // Get source Slot
        sourceSlot = null;
        var gotSourceSlot = false;

        foreach (var slot in sourceSlotList)
        {
            if (slot.Id != connection.SourceSlotId)
                continue;

            sourceSlot = slot;
            gotSourceSlot = true;
            break;
        }

        return gotSourceSlot;
    }

    internal bool TryAddConnection(Symbol.Connection connection, int multiInputIndex, bool allowCreate)
    {
        if (!TryGetSourceSlot(connection, out var sourceSlot, allowCreate) ||
            !TryGetTargetSlot(connection, out var targetSlot, allowCreate))
            return false;

        targetSlot.AddConnection(sourceSlot, multiInputIndex);
        sourceSlot.DirtyFlag.Invalidate();
        return true;
    }

    private protected void SetupInputAndOutputsFromType()
    {
        var symbol = Symbol;
        var assemblyInfo = symbol.SymbolPackage.AssemblyInformation;
        if (!assemblyInfo.OperatorTypeInfo.TryGetValue(symbol.Id, out var operatorTypeInfo))
        {
            Log.Error($"Can't find operatorTypeInfo for id {symbol} {symbol.Id} in {assemblyInfo}");
            Debug.Assert(false);
        }

        //var operatorTypeInfo = assemblyInfo.OperatorTypeInfo[symbol.Id];
        foreach (var input in operatorTypeInfo.Inputs)
        {
            var attribute = input.Attribute;
            var inputSlot = input.GetSlotObject(this);
            inputSlot.Parent = this;
            inputSlot.Id = attribute.Id;
            inputSlot.MappedType = attribute.MappedType;
            _inputs.Add(inputSlot);
        }

        // outputs identified by attribute
        foreach (var output in operatorTypeInfo.Outputs)
        {
            var slot = output.GetSlotObject(this);
            slot.Parent = this;
            slot.Id = output.Attribute.Id;
            _outputs.Add(slot);
        }
    }

    internal static bool SetBypassFor(Instance instance, bool shouldBypass, bool invalidate = true)
    {
        var mainInputSlot = instance.Inputs[0];
        var mainOutputSlot = instance.Outputs[0];

        var wasByPassed = false;

        // note - can this be made more flexible by not having a "main" input/output requirement and instead
        // matching any one-to-one input/output type pairs?
        
        switch (mainOutputSlot)
        {
            case Slot<Command> commandOutput when mainInputSlot is Slot<Command> commandInput:
                if (shouldBypass)
                {
                    wasByPassed = commandOutput.TrySetBypassToInput(commandInput);
                }
                else
                {
                    commandOutput.RestoreUpdateAction();
                }

                if (invalidate)
                    InvalidateConnected(commandInput);
                break;

            case Slot<BufferWithViews> bufferOutput when mainInputSlot is Slot<BufferWithViews> bufferInput:
                if (shouldBypass)
                {
                    wasByPassed = bufferOutput.TrySetBypassToInput(bufferInput);
                }
                else
                {
                    bufferOutput.RestoreUpdateAction();
                }

                if (invalidate)
                    InvalidateConnected(bufferInput);

                break;
            case Slot<MeshBuffers> bufferOutput when mainInputSlot is Slot<MeshBuffers> bufferInput:
                if (shouldBypass)
                {
                    wasByPassed = bufferOutput.TrySetBypassToInput(bufferInput);
                }
                else
                {
                    bufferOutput.RestoreUpdateAction();
                }

                if (invalidate)
                    InvalidateConnected(bufferInput);

                break;
            case Slot<Texture2D> texture2dOutput when mainInputSlot is Slot<Texture2D> texture2dInput:
                if (shouldBypass)
                {
                    wasByPassed = texture2dOutput.TrySetBypassToInput(texture2dInput);
                }
                else
                {
                    texture2dOutput.RestoreUpdateAction();
                }

                if (invalidate)
                    InvalidateConnected(texture2dInput);

                break;
            case Slot<float> floatOutput when mainInputSlot is Slot<float> floatInput:
                if (shouldBypass)
                {
                    wasByPassed = floatOutput.TrySetBypassToInput(floatInput);
                }
                else
                {
                    floatOutput.RestoreUpdateAction();
                }

                if (invalidate)
                    InvalidateConnected(floatInput);

                break;

            case Slot<Vector2> vec2Output when mainInputSlot is Slot<Vector2> vec2Input:
                if (shouldBypass)
                {
                    wasByPassed = vec2Output.TrySetBypassToInput(vec2Input);
                }
                else
                {
                    vec2Output.RestoreUpdateAction();
                }

                if (invalidate)
                    InvalidateConnected(vec2Input);

                break;
            case Slot<Vector3> vec3Output when mainInputSlot is Slot<Vector3> vec3Input:
                if (shouldBypass)
                {
                    wasByPassed = vec3Output.TrySetBypassToInput(vec3Input);
                }
                else
                {
                    vec3Output.RestoreUpdateAction();
                }

                if (invalidate)
                    InvalidateConnected(vec3Input);

                break;
            case Slot<string> stringOutput when mainInputSlot is Slot<string> stringInput:
                if (shouldBypass)
                {
                    wasByPassed = stringOutput.TrySetBypassToInput(stringInput);
                }
                else
                {
                    stringOutput.RestoreUpdateAction();
                }

                if (invalidate)
                    InvalidateConnected(stringInput);
                break;
        }
        
        if(wasByPassed)
            instance._status |= InstanceStatus.Bypassed;
        else
            instance._status &= ~InstanceStatus.Bypassed;

        return wasByPassed;
        
        static void InvalidateConnected<T>(Slot<T> bufferInput)
        {
            if (bufferInput.TryGetAsMultiInputTyped(out var multiInput))
            {
                foreach (var connection in multiInput.CollectedInputs)
                {
                    InvalidateParentInputs(connection);
                }
            }
            else
            {
                var connection = bufferInput.FirstConnection;
                InvalidateParentInputs(connection);
            }

            return;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            void InvalidateParentInputs(ISlot connection)
            {
                if (connection.ValueType == typeof(string))
                    return;

                connection.DirtyFlag.Invalidate();
            }
        }
    }

    private void MarkNeedsConnections()
    {
        _status &= ~InstanceStatus.ConnectedInternally;

        if (TryGetParentInstance(out var parent, false))
        {
            parent.MarkNeedsConnections();
        }
    }
}