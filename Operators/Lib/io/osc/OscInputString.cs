using System.Net.NetworkInformation;
using System.Net.Sockets;
using Operators.Utils;
using Rug.Osc;
using T3.Core.Operator;
using T3.Core.Operator.Attributes;
using T3.Core.Operator.Slots;
using T3.Core.Utils;

namespace Lib.io.osc;

[Guid("2F81B7A8-2437-4442-992A-48A5955A56EF")]
internal sealed class OscInputString : Instance<OscInputString>, OscConnectionManager.IOscConsumer, IStatusProvider
{
    [Output(Guid = "C5283362-A1A2-462A-994A-83424955E8A5", DirtyFlagTrigger = DirtyFlagTrigger.Always)]
    public readonly Slot<string> Result = new();

    [Output(Guid = "A525256E-42B3-4E9A-82E1-A68E79A5353F", DirtyFlagTrigger = DirtyFlagTrigger.Animated)]
    public readonly Slot<bool> WasTrigger = new();

    public OscInputString()
    {
        Result.UpdateAction += Update;
        WasTrigger.UpdateAction += AnimatedUpdate;
    }

    private void Update(EvaluationContext context)
    {
        UpdateConnection(context);
        Result.Value = _lastMessageContent;
    }

    private void AnimatedUpdate(EvaluationContext context)
    {
        if (Math.Abs(_lastUpdateFrame - context.LocalFxTime) < 0.001f)
            return;

        _lastUpdateFrame = context.LocalFxTime;
        
        UpdateConnection(context); // Keep connection settings up-to-date

        WasTrigger.Value = _wasTrigger;
        _wasTrigger = false;
    }

    private void UpdateConnection(EvaluationContext context)
    {
        _address = Address.GetValue(context);
        
        var newPort = Port.GetValue(context);
        var portChanged = newPort != _port;
        
        var isListening = IsListening.GetValue(context);
        var isListeningChanged = isListening != _isListening;

        if (portChanged || isListeningChanged)
        {
            if (newPort < 0 || newPort > 65535)
            {
                SetStatus("Invalid port number", IStatusProvider.StatusLevel.Warning);
                return;
            }

            if (_isConnected)
            {
                OscConnectionManager.UnregisterConsumer(this);
                _isConnected = false;
            }

            if (isListening)
            {
                OscConnectionManager.RegisterConsumer(this, newPort);
                _isConnected = true;
            }

            _port = newPort;
            _isListening = isListening;
            UpdateStatusMessage();
        }
    }

    public void ProcessMessage(OscMessage msg)
    {
        lock (this)
        {
            // Only process if the address matches
            if (msg.Address != _address)
                return;

            if (msg.Count > 0 && msg[0] != null)
            {
                _lastMessageContent = msg[0].ToString();
                Result.DirtyFlag.Invalidate();
                _wasTrigger = true;
            }
        }
    }

    protected override void Dispose(bool isDisposing)
    {
        if (isDisposing && _isConnected)
        {
            OscConnectionManager.UnregisterConsumer(this);
        }
    }
    
    #region Status Provider and boilerplate
    private void UpdateStatusMessage()
    {
        if (_isConnected)
        {
            SetStatus($"Listening on port {_port} for address '{_address}'", IStatusProvider.StatusLevel.Success);
        }
        else
        {
            SetStatus("Not listening", IStatusProvider.StatusLevel.Notice);
        }
    }
    
    private void SetStatus(string message, IStatusProvider.StatusLevel level)
    {
        _lastWarningMessage = message;
        _statusLevel = level;
    }

    public IStatusProvider.StatusLevel GetStatusLevel() => _statusLevel;
    public string GetStatusMessage() => _lastWarningMessage;

    private string _lastWarningMessage = "Not updated yet.";
    private IStatusProvider.StatusLevel _statusLevel;
    
    private double _lastUpdateFrame;
    private bool _wasTrigger;
    private string _lastMessageContent = string.Empty;
    private bool _isConnected;
    private int _port = -1;
    private bool _isListening;
    private string _address;

    [Input(Guid = "D42632A3-64AF-4E73-836A-8DF39355B0B9")]
    public readonly InputSlot<int> Port = new();
    
    [Input(Guid = "E6F822C0-C851-47C2-AA73-0F84C6F8CCCC")]
    public readonly InputSlot<string> Address = new();
    
    [Input(Guid = "B4A9C2F5-84A3-493B-B79A-56E3A1A248B8")]
    public readonly InputSlot<bool> IsListening = new();
    #endregion
}