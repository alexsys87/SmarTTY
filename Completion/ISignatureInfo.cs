
namespace SmarTTY.Completion;

public interface ISignatureInfo
{
    int ParameterCount { get; }

    string Separator { get; }

    bool IsVertical { get; }

    ISignatureParameter GetParameter(int value2);
}
