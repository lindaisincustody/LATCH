using System.Collections.Generic;

/// <summary>
/// The label set the ONNX model was trained on, and the glyphs used to show them.
/// Order matters: index N here must match output neuron N of the model.
/// </summary>
public static class SymbolLibrary
{
    public static readonly string[] Labels =
    {
        "_Aries", "_Capricorn", "_Cross", "_EyesDollar", "_Heart", "_Leo", "_Mercury",
        "_Moon", "_Rightarrow", "_Sigma", "_Taurus", "_alpha", "_bigtriangleup",
        "_bowtie", "_boxplus", "_circlearrowleft", "_clubsuit", "_diagup",
        "_diamondsuit", "_downarrow", "_emptyset", "_female", "_infty", "_lambda",
        "_lightning", "_ltimes", "_male", "_psi", "_sim", "_spadesuit", "_square",
        "_star", "_textasteriskcentered", "_textcent", "_textgamma",
        "_textmusicalnote", "_theta", "_varphi"
    };

    static readonly Dictionary<string, string> Glyphs = new Dictionary<string, string>
    {
        { "_Aries", "♈" },
        { "_Capricorn", "♑" },
        { "_Cross", "†" },
        { "_EyesDollar", "$" },
        { "_Heart", "♥" },
        { "_Leo", "♌" },
        { "_Mercury", "☿" },
        { "_Moon", "☾" },
        { "_Rightarrow", "⇒" },
        { "_Sigma", "Σ" },
        { "_Taurus", "♉" },
        { "_alpha", "α" },
        { "_bigtriangleup", "△" },
        { "_bowtie", "⋈" },
        { "_boxplus", "⊞" },
        { "_circlearrowleft", "↺" },
        { "_clubsuit", "♣" },
        { "_diagup", "/" },
        { "_diamondsuit", "♦" },
        { "_downarrow", "↓" },
        { "_emptyset", "∅" },
        { "_female", "♀" },
        { "_infty", "∞" },
        { "_lambda", "λ" },
        { "_lightning", "⚡" },
        { "_ltimes", "⋉" },
        { "_male", "♂" },
        { "_psi", "ψ" },
        { "_sim", "∼" },
        { "_spadesuit", "♠" },
        { "_square", "■" },
        { "_star", "★" },
        { "_textasteriskcentered", "∗" },
        { "_textcent", "¢" },
        { "_textgamma", "γ" },
        { "_textmusicalnote", "♪" },
        { "_theta", "θ" },
        { "_varphi", "φ" }
    };

    /// <summary>Display glyph for a label, falling back to the raw label if unmapped.</summary>
    public static string ToGlyph(string label)
    {
        if (string.IsNullOrEmpty(label)) return "?";
        return Glyphs.TryGetValue(label, out string glyph) ? glyph : label;
    }

    public static bool IsValidLabel(string label)
    {
        return !string.IsNullOrEmpty(label) && Glyphs.ContainsKey(label);
    }
}
