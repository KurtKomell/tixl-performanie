using T3.SystemUi;

namespace T3.Core.SystemUi;

public static class BlockingWindow
{
    private static IMessageBoxProvider? _instance;
    public static IMessageBoxProvider Instance
    
        {
            get => _instance ?? throw new Exception($"{typeof(BlockingWindow)}'s {nameof(Instance)} is not set.");
            set => _instance = value; // Erlaube das Zurücksetzen
        }
    
}