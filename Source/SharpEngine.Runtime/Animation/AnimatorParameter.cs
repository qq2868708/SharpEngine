namespace SharpEngine.Animation;

/// <summary>Serialized numeric override; omitted IDs use the controller's explicit defaults.</summary>
[Serializable]
public sealed partial class AnimatorParameter
{
    public string ParameterId = "";
    public double Value;
}
