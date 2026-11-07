using System.Numerics;
using SharpEngine.Animation;
using SharpEngine.Editor;
using SharpEngine.Mathematics;
using SharpEngine.UI;
using SharpEngine.World;

internal static class SkeletonGizmoValidation
{
    internal static void Run(Action<bool, string> Check)
    {
        var Bounds = new UiRect(125, 70, 800, 600);
        var Navigation = new OrbitCameraController();
        Navigation.Frame([new Vector3(-2), new Vector3(2)]);
        var Camera = Navigation.Camera;
        var Parent = Matrix4x4.CreateScale(2, 0.5f, 1.4f) *
            Matrix4x4.CreateFromQuaternion(QuaternionMath.Euler(new Vector3(15, 40, -20))) *
            Matrix4x4.CreateTranslation(0.3f, 0.1f, -0.2f);
        var Socket = new SkeletonSocket("attachment", "Weapon", "virtual-parent",
            new BoneTransform(new Vector3(0.2f, -0.1f, 0.3f),
                QuaternionMath.Euler(new Vector3(25, 30, 10)), new Vector3(1.2f, 0.8f, 1.1f)));
        Matrix4x4 Model(BoneTransform Local) => Matrix4x4.CreateScale(Local.Scale) *
            Matrix4x4.CreateFromQuaternion(Local.Rotation) * Matrix4x4.CreateTranslation(Local.Position) * Parent;
        foreach (var Tool in new[]
        {
            "move", "rotate", "scale",
        })
        {
            var Gizmo = new TransformGizmo(Model(Socket.LocalTransform), Camera, Bounds, Tool);
            Check(Gizmo.Handles.Count == 3 && Gizmo.BuildLines().Count >= 18,
                "Shared " + Tool + " geometry renders without a World or entity");
            Check(!Gizmo.TryPick(Bounds.X - 1, Bounds.Y, out _) && !Gizmo.TryPick(float.NaN, 0, out _),
                "Shared Gizmo rejects clipped and non-finite pointer coordinates");
            foreach (var Handle in Gizmo.Handles)
            {
                var Path = Gizmo.GetHandlePath(Handle);
                var Point = Tool == "rotate" ? Path[8] : Handle.End;
                var Hit = Gizmo.TryPick(Point.X + Bounds.X, Point.Y + Bounds.Y, out var Picked);
                Check(Hit && Picked.Mode == Handle.Mode,
                    "Shared " + Tool + " drawing and host-offset picking agree");
                var StartPointer = Tool == "rotate" ? Point : Handle.End;
                var DragHandle = Handle with
                {
                    End = Tool == "rotate" ? Point : Handle.End,
                };
                var Drag = new SkeletonSocketGizmoDrag(Socket, Parent, DragHandle, StartPointer, 17);
                var Direction = DragHandle.ScreenDirection;
                var Delta = Tool == "rotate" ? new Vector2(Direction.Y, -Direction.X) * 40 : Direction * 40;
                var Result = Drag.Evaluate(StartPointer + Delta);
                Check(Drag.Evaluate(StartPointer) == Socket.LocalTransform &&
                    Drag.Evaluate(StartPointer + Delta) == Result,
                    "A " + Tool + " drag evaluates from its starting state and returns exactly on a zero delta");
                if (Tool == "move")
                {
                    var ModelDelta = Model(Result).Translation - Model(Socket.LocalTransform).Translation;
                    Check(Vector3.Cross(Vector3.Normalize(ModelDelta), Handle.WorldDirection).Length() < 0.0001f &&
                        Result.Rotation == Socket.LocalTransform.Rotation && Result.Scale == Socket.LocalTransform.Scale,
                        "Move preserves TRS and follows the displayed axis through a rotated non-uniform parent");
                }
                else if (Tool == "rotate")
                {
                    Check(Math.Abs(Result.Rotation.LengthSquared() - 1) < 0.0001f &&
                        Quaternion.Dot(Result.Rotation, Socket.LocalTransform.Rotation) < 0.9999f &&
                        Result.Position == Socket.LocalTransform.Position && Result.Scale == Socket.LocalTransform.Scale,
                        "Rotate changes the local quaternion without decomposing the sheared model matrix");
                }
                else
                {
                    var Before = Socket.LocalTransform.Scale;
                    var ChangedComponents = new[]
                    {
                        Result.Scale.X != Before.X, Result.Scale.Y != Before.Y, Result.Scale.Z != Before.Z,
                    };
                    Check(ChangedComponents.Count(Value => Value) == 1 &&
                        Result.Position == Socket.LocalTransform.Position && Result.Rotation == Socket.LocalTransform.Rotation,
                        "Scale affects exactly one local component under a non-uniform parent");
                    var Clamped = Drag.Evaluate(StartPointer - Direction * 10000);
                    Check(Clamped.Scale.X > 0 && Clamped.Scale.Y > 0 && Clamped.Scale.Z > 0,
                        "Scale dragging keeps every component positive");
                }
            }
            var BeforeLines = Gizmo.BuildLines().ToArray();
            _ = new TransformGizmo(Matrix4x4.CreateTranslation(10, 0, 0), Camera,
                new UiRect(0, 0, 100, 100), "rotate").BuildLines();
            Check(BeforeLines.SequenceEqual(Gizmo.BuildLines()), "Independent Gizmo instances retain isolated geometry");
        }
        var Hidden = new TransformGizmo(Matrix4x4.CreateTranslation(Camera.Position - Camera.Forward),
            Camera, Bounds);
        Check(Hidden.Handles.Count == 0 && Hidden.BuildLines().Count == 0,
            "A Gizmo behind its observation camera has no drawing or hit target");
        var Empty = new TransformGizmo(Matrix4x4.Identity, Camera, new UiRect(0, 0, 0, 0));
        Check(Empty.Handles.Count == 0, "A zero-size viewport has no Gizmo handles");
    }
}
