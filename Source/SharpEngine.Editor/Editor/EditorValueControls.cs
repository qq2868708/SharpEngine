using System.Numerics;
using System.Text.Json.Nodes;
using SharpEngine.UI;

namespace SharpEngine.Editor;

/// <summary>Editor binding adapter for reusable UI value controls. Asset/domain validation remains with the owner.</summary>
internal static class EditorValueControls
{
    internal static string Transform(string IdPrefix, Vector3 Position, Vector3 Rotation, Vector3 Scale,
        Func<string, int, string> CommitAction, bool ReadOnly)
    {
        return UiValueEditors.Transform("Transform", Inputs("position", Position), Inputs("rotation", Rotation), Inputs("scale", Scale));

        UiValueInput[] Inputs(string Field, Vector3 Value)
        {
            var Numbers = new[]
            {
                Value.X, Value.Y, Value.Z,
            };
            return Numbers.Select((Number, Index) => new UiValueInput(IdPrefix + Field + "-" + "XYZ"[Index],
                UiValueEditors.Format(Number), CommitAction(Field, Index), ReadOnly, "number")).ToArray();
        }
    }

    internal static void SetVectorAxis(JsonObject Node, string Field, int Axis, float Value, bool Rotation)
    {
        if (Axis < 0 || Axis > 2 || !float.IsFinite(Value))
        {
            throw new InvalidDataException("VALUE_AXIS_INVALID: expected a finite value for X, Y or Z");
        }
        if (Rotation)
        {
            var Values = Node[Field]!.AsArray();
            var Quaternion = new Quaternion(Values[0]!.GetValue<float>(), Values[1]!.GetValue<float>(),
                Values[2]!.GetValue<float>(), Values[3]!.GetValue<float>());
            var Euler = SharpEngine.Mathematics.QuaternionMath.ToEuler(Quaternion);
            var Axes = new[]
            {
                Euler.X, Euler.Y, Euler.Z,
            };
            Axes[Axis] = Value;
            var Result = SharpEngine.Mathematics.QuaternionMath.Euler(new Vector3(Axes[0], Axes[1], Axes[2]));
            Node[Field] = new JsonArray(Result.X, Result.Y, Result.Z, Result.W);
        }
        else
        {
            Node[Field]!.AsArray()[Axis] = Value;
        }
    }
}
