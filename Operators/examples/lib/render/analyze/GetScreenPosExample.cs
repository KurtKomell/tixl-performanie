using T3.Core.DataTypes;
using T3.Core.Operator;
using T3.Core.Operator.Attributes;
using T3.Core.Operator.Slots;
using System.Runtime.InteropServices;

namespace Examples.lib.render.analyze{
    [Guid("f0ea45cd-9971-46cd-a761-6e81c036aa87")]
    internal sealed class GetScreenPosExample : Instance<GetScreenPosExample>
    {
        [Output(Guid = "b8823995-0087-41c3-9e8f-89c6024c23b1")]
        public readonly Slot<Texture2D> ColorBuffer = new Slot<Texture2D>();


    }
}

