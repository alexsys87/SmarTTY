namespace SmarTTY.Completion;

internal interface IHintView
{
    string SignatureText { get; set; }

    string Description { get; set; }

    bool ShowDescription { get; set; }

    bool Visible { get; }

    void SetActiveParameter(int index);

    void SetSignature(ISignatureInfo signatureInfo);

    void Dismiss();

    void ShowAt(ICompletionHost completionHost, bool isVertical);

    void Reposition(ICompletionHost completionHost, bool isVertical);
}
