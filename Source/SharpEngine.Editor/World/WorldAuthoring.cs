using System.Numerics;
using SharpEngine.World;
using SharpEngine.Components;
using System.Text.Json.Nodes;

namespace SharpEngine.World;

public enum WorldHierarchyPlacement : byte
{
    Root,
    Inside,
    Before,
    After,
}

public readonly record struct WorldLocalTransform(
    WorldVector3 Position, WorldVector3 Rotation, WorldVector3 Scale);

public sealed record WorldReparentRequest(
    string SourceEntityId,
    string? NewParentEntityId,
    WorldHierarchyPlacement Placement,
    string? AnchorEntityId,
    ulong ExpectedRevision,
    bool PreserveWorldTransform = true);

public sealed record WorldTransformRequest(
    string EntityId,
    WorldLocalTransform Transform,
    ulong ExpectedRevision,
    string? TransactionGroupId = null);

public sealed record WorldTransactionRecord(
    Guid TransactionId,
    string Kind,
    ulong BeforeRevision,
    ulong AfterRevision,
    IReadOnlyList<string> AffectedEntityIds);

public sealed record WorldEditResult(
    bool Success,
    string Code,
    string Message,
    ulong BeforeRevision,
    ulong AfterRevision,
    bool IsDirty,
    WorldTransactionRecord? Transaction = null)
{
    public static WorldEditResult Failure(string code, string message,
        ulong revision, bool isDirty) =>
        new(false, code, message, revision, revision, isDirty);
}

/// <summary>
/// Owns Authoring mutations for one world document. Call it only from the editor
/// thread. Every successful mutation is revisioned and joins the shared history.
/// </summary>
public sealed class WorldAuthoringSession
{
    private const float MatrixTolerance = 0.0002f;
    private readonly int _ownerThreadId = Environment.CurrentManagedThreadId;
    private readonly List<HistoryEntry> _history = new();
    private string _savepoint;
    private int _historyCursor;

    public WorldAuthoringSession(WorldDocument document, ulong initialRevision = 1)
    {
        Document = document ?? throw new ArgumentNullException(nameof(document));
        DocumentRevision = initialRevision;
        SavepointRevision = initialRevision;
        _savepoint = Serialize(document);
    }

    public WorldDocument Document { get; }
    public ulong DocumentRevision { get; private set; }
    public ulong SavepointRevision { get; private set; }
    public bool IsDirty => !string.Equals(_savepoint, Serialize(Document), StringComparison.Ordinal);
    public bool CanUndo => _historyCursor > 0;
    public bool CanRedo => _historyCursor < _history.Count;
    internal void ValidateRegistry(SharpEngine.Components.ComponentRegistry registry)
    {
        EnsureOwnerThread();
        Validate(SceneData.Parse(Document.Serialize(), registry));
        foreach (var entry in _history)
        {
            Validate(SceneData.Parse(entry.Before.Json, registry));
            Validate(SceneData.Parse(entry.After.Json, registry));
        }
        void Validate(SceneData data)
        {
            foreach (var component in data.Entities.SelectMany(entity => entity.Components))
                if (Document.Registry.Find(component.TypeId) is not null && registry.Find(component.TypeId) is null)
                    throw new InvalidDataException($"Reload would remove component type {component.TypeId} used by {Document.SourcePath} or its history");
            data.Validate(registry);
        }
    }
    internal Action CaptureReloadRestorePoint()
    {
        EnsureOwnerThread();
        var data = Document.Data.Clone();
        var registry = Document.Registry;
        var history = _history.ToArray();
        var cursor = _historyCursor;
        var savepoint = _savepoint;
        var revision = DocumentRevision;
        var savedRevision = SavepointRevision;
        return () =>
        {
            Document.Registry = registry;
            Document.Replace(data.Clone());
            _history.Clear(); _history.AddRange(history); _historyCursor = cursor;
            _savepoint = savepoint; DocumentRevision = revision; SavepointRevision = savedRevision;
        };
    }
    public string? UndoDescription => CanUndo ? Describe(_history[_historyCursor - 1].Kind) : null;
    public string? RedoDescription => CanRedo ? Describe(_history[_historyCursor].Kind) : null;

    public void Save(string path)
    {
        EnsureOwnerThread();
        Document.Save(path);
        MarkSavepoint();
    }

    public void MarkSavepoint()
    {
        EnsureOwnerThread();
        _savepoint = Serialize(Document);
        SavepointRevision = DocumentRevision;
    }

    public WorldEditResult Reparent(WorldReparentRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!IsOwnerThread()) return WrongThread();
        if (request.ExpectedRevision != DocumentRevision) return RevisionConflict(request.ExpectedRevision);

        var source = Find(request.SourceEntityId);
        if (source is null) return Failure("ENTITY_NOT_FOUND",
            $"Source entity '{request.SourceEntityId}' does not exist");

        var destination = ResolveDestination(request, source);
        if (!destination.Success) return Failure(destination.Code, destination.Message);
        var newParentId = destination.ParentId;
        if (CreatesCycle(source.Id, newParentId)) return Failure("PARENT_CYCLE",
            $"Entity '{source.Id}' cannot be parented to itself or one of its descendants");

        var oldWorld = Document.WorldMatrix(source);
        var newTransform = CurrentTransform(source);
        if (request.PreserveWorldTransform && !SameId(source.ParentId, newParentId) &&
            !TryGetLocalTransform(oldWorld, newParentId, out newTransform, out var transformError))
            return Failure("TRANSFORM_NOT_DECOMPOSABLE", transformError);

        var order = BuildOrder(source, newParentId, request.Placement, destination.Anchor);
        var before = Capture();
        try
        {
            source.ParentId = newParentId;
            ApplyTransform(source, newTransform);
            Document.SetEntityOrder(order);
            ValidateHierarchy();
        }
        catch (Exception exception)
        {
            Apply(before);
            return Failure("REPARENT_REJECTED", exception.Message);
        }

        var after = Capture();
        return Commit("reparent", before, after, [source.Id]);
    }

    public WorldEditResult SetTransform(WorldTransformRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!IsOwnerThread()) return WrongThread();
        if (request.ExpectedRevision != DocumentRevision) return RevisionConflict(request.ExpectedRevision);
        var entity = Find(request.EntityId);
        if (entity is null) return Failure("ENTITY_NOT_FOUND",
            $"Entity '{request.EntityId}' does not exist");
        if (!IsValid(request.Transform)) return Failure("INVALID_TRANSFORM",
            "Transform values must be finite and scale must be positive");

        var before = Capture();
        ApplyTransform(entity, request.Transform);
        var after = Capture();
        return Commit("transform", before, after, [entity.Id], request.TransactionGroupId);
    }

    public WorldEditResult Undo(ulong expectedRevision)
    {
        if (!IsOwnerThread()) return WrongThread();
        if (expectedRevision != DocumentRevision) return RevisionConflict(expectedRevision);
        if (!CanUndo) return Failure("NOTHING_TO_UNDO", "There is no Authoring transaction to undo");
        var entry = _history[_historyCursor - 1];
        var beforeRevision = DocumentRevision;
        Apply(entry.Before);
        _historyCursor--;
        DocumentRevision++;
        var record = new WorldTransactionRecord(Guid.NewGuid(), "undo", beforeRevision,
            DocumentRevision, entry.AffectedEntityIds);
        return Success("UNDO_APPLIED", "Authoring transaction undone", record);
    }

    public WorldEditResult Redo(ulong expectedRevision)
    {
        if (!IsOwnerThread()) return WrongThread();
        if (expectedRevision != DocumentRevision) return RevisionConflict(expectedRevision);
        if (!CanRedo) return Failure("NOTHING_TO_REDO", "There is no Authoring transaction to redo");
        var entry = _history[_historyCursor];
        var beforeRevision = DocumentRevision;
        Apply(entry.After);
        _historyCursor++;
        DocumentRevision++;
        var record = new WorldTransactionRecord(Guid.NewGuid(), "redo", beforeRevision,
            DocumentRevision, entry.AffectedEntityIds);
        return Success("REDO_APPLIED", "Authoring transaction redone", record);
    }

    private Destination ResolveDestination(WorldReparentRequest request, WorldEntity source)
    {
        var requestedParent = NormalizeId(request.NewParentEntityId);
        var anchorId = NormalizeId(request.AnchorEntityId);
        switch (request.Placement)
        {
            case WorldHierarchyPlacement.Root:
                if (requestedParent.Length != 0 || anchorId.Length != 0)
                    return Destination.Fail("INVALID_PLACEMENT",
                        "Root placement does not accept a parent or anchor");
                return Destination.Ok(string.Empty, null);
            case WorldHierarchyPlacement.Inside:
                if (requestedParent.Length == 0 || anchorId.Length != 0)
                    return Destination.Fail("INVALID_PLACEMENT",
                        "Inside placement requires a parent and no anchor");
                var parent = Find(requestedParent);
                if (parent is null) return Destination.Fail("PARENT_NOT_FOUND",
                    $"Parent entity '{requestedParent}' does not exist");
                return Destination.Ok(parent.Id, null);
            case WorldHierarchyPlacement.Before:
            case WorldHierarchyPlacement.After:
                if (anchorId.Length == 0)
                    return Destination.Fail("INVALID_PLACEMENT",
                        "Before/after placement requires an anchor entity");
                var anchor = Find(anchorId);
                if (anchor is null) return Destination.Fail("ANCHOR_NOT_FOUND",
                    $"Anchor entity '{anchorId}' does not exist");
                if (ReferenceEquals(anchor, source)) return Destination.Fail("INVALID_ANCHOR",
                    "An entity cannot be ordered relative to itself");
                if (requestedParent.Length != 0 && !SameId(requestedParent, anchor.ParentId))
                    return Destination.Fail("PARENT_ANCHOR_MISMATCH",
                        "Requested parent does not match the anchor's parent");
                return Destination.Ok(anchor.ParentId, anchor);
            default:
                return Destination.Fail("INVALID_PLACEMENT", "Unknown hierarchy placement");
        }
    }

    private IReadOnlyList<WorldEntity> BuildOrder(WorldEntity source, string newParentId,
        WorldHierarchyPlacement placement, WorldEntity? anchor)
    {
        var order = Document.Entities.Where(entity => !ReferenceEquals(entity, source)).ToList();
        if (anchor is not null)
        {
            var anchorIndex = order.IndexOf(anchor);
            order.Insert(placement == WorldHierarchyPlacement.After ? anchorIndex + 1 : anchorIndex, source);
            return order;
        }

        var lastSibling = order.FindLastIndex(entity => SameId(entity.ParentId, newParentId));
        if (lastSibling >= 0)
        {
            order.Insert(lastSibling + 1, source);
            return order;
        }
        if (newParentId.Length != 0)
        {
            var parentIndex = order.FindIndex(entity => SameId(entity.Id, newParentId));
            order.Insert(parentIndex + 1, source);
            return order;
        }
        order.Add(source);
        return order;
    }

    private bool TryGetLocalTransform(Matrix4x4 oldWorld, string newParentId,
        out WorldLocalTransform transform, out string error)
    {
        var local = oldWorld;
        if (newParentId.Length != 0)
        {
            var parent = Find(newParentId)!;
            if (!Matrix4x4.Invert(Document.WorldMatrix(parent), out var inverseParent))
            {
                transform = default;
                error = $"Parent entity '{newParentId}' has a non-invertible world transform";
                return false;
            }
            local = oldWorld * inverseParent;
        }
        return TryDecompose(local, out transform, out error);
    }

    private static bool TryDecompose(Matrix4x4 matrix, out WorldLocalTransform transform,
        out string error)
    {
        if (!Matrix4x4.Decompose(matrix, out var scale, out var rotation, out var position) ||
            !IsFinite(scale) || !IsFinite(rotation) || !IsFinite(position) ||
            scale.X <= 0 || scale.Y <= 0 || scale.Z <= 0)
        {
            transform = default;
            error = "The preserved world transform cannot be represented by positive local TRS";
            return false;
        }

        rotation = Quaternion.Normalize(rotation);
        var euler = QuaternionToEuler(rotation);
        transform = new WorldLocalTransform(ToWorld(position), euler, ToWorld(scale));
        var recomposed = Compose(transform);
        if (!NearlyEqual(matrix, recomposed))
        {
            transform = default;
            error = "The preserved world transform would introduce shear or lose rotation precision";
            return false;
        }
        error = string.Empty;
        return true;
    }

    private WorldEditResult Commit(string kind, DocumentState before, DocumentState after,
        IReadOnlyList<string> affected, string? transactionGroupId = null)
    {
        var beforeRevision = DocumentRevision;
        if (before.Equals(after)) return new WorldEditResult(true, "NO_CHANGE",
            "The requested edit did not change the world", beforeRevision, beforeRevision, IsDirty);

        DocumentRevision++;
        if (_historyCursor < _history.Count)
            _history.RemoveRange(_historyCursor, _history.Count - _historyCursor);

        HistoryEntry entry;
        if (!string.IsNullOrWhiteSpace(transactionGroupId) && _historyCursor > 0 &&
            _history[^1].Kind == kind && _history[^1].TransactionGroupId == transactionGroupId &&
            _history[^1].AffectedEntityIds.SequenceEqual(affected, StringComparer.OrdinalIgnoreCase))
        {
            entry = _history[^1] with { After = after, AfterRevision = DocumentRevision };
            _history[^1] = entry;
        }
        else
        {
            entry = new HistoryEntry(Guid.NewGuid(), kind, transactionGroupId, before, after,
                beforeRevision, DocumentRevision, affected.ToArray());
            _history.Add(entry);
            _historyCursor++;
        }

        var record = new WorldTransactionRecord(entry.TransactionId, kind,
            entry.BeforeRevision, DocumentRevision, entry.AffectedEntityIds);
        return new WorldEditResult(true, "EDIT_COMMITTED", "Authoring edit committed",
            beforeRevision, DocumentRevision, IsDirty, record);
    }

    private DocumentState Capture() => new(Document.Serialize());

    private void Apply(DocumentState state)
    {
        Document.Replace(SceneData.Parse(state.Json, Document.Registry));
    }

    public WorldEditResult Edit(string kind, ulong expectedRevision, Action<SceneData> edit,
        string[] affected, string? transactionGroupId = null)
    {
        if (!IsOwnerThread()) return WrongThread();
        if (expectedRevision != DocumentRevision) return RevisionConflict(expectedRevision);
        var before = Capture();
        var staging = Document.Data.Clone();
        try { edit(staging); staging.Validate(Document.Registry); }
        catch (Exception error) { return Failure("EDIT_REJECTED", error.Message); }
        Document.Replace(staging);
        return Commit(kind, before, Capture(), affected, transactionGroupId);
    }

    public WorldEditResult PatchComponent(string entityId, string componentId, JsonObject patch,
        ulong expectedRevision, string? transactionGroupId = null) => Edit("component.patch", expectedRevision, data =>
    {
        var component = data.Entities.Single(e => e.EntityId == entityId).Components.Single(c => c.ComponentId == componentId);
        if (Document.Registry.Find(component.TypeId) is null) throw new InvalidOperationException("Unknown component cannot be edited");
        foreach (var (field, value) in patch) component.State[field] = value?.DeepClone();
    }, [entityId], transactionGroupId);

    private void ValidateHierarchy()
    {
        foreach (var entity in Document.Entities)
        {
            var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var current = entity;
            while (current.ParentId.Length != 0)
            {
                if (!visiting.Add(current.Id))
                    throw new InvalidDataException($"World parent cycle includes '{current.Id}'");
                current = Find(current.ParentId) ?? throw new InvalidDataException(
                    $"World entity '{current.Id}' references missing parent '{current.ParentId}'");
            }
        }
    }

    private bool CreatesCycle(string sourceId, string parentId)
    {
        for (var currentId = parentId; currentId.Length != 0;)
        {
            if (SameId(sourceId, currentId)) return true;
            var current = Find(currentId);
            if (current is null) return false;
            currentId = current.ParentId;
        }
        return false;
    }

    private WorldEntity? Find(string? id)
    {
        var normalized = NormalizeId(id);
        return normalized.Length == 0 ? null : Document.Entities.FirstOrDefault(entity => SameId(entity.Id, normalized));
    }

    private WorldEditResult Success(string code, string message, WorldTransactionRecord record) =>
        new(true, code, message, record.BeforeRevision, record.AfterRevision, IsDirty, record);

    private WorldEditResult Failure(string code, string message) =>
        WorldEditResult.Failure(code, message, DocumentRevision, IsDirty);

    private WorldEditResult RevisionConflict(ulong actualExpected) => Failure("REVISION_CONFLICT",
        $"Expected document revision {actualExpected}, current revision is {DocumentRevision}");

    private WorldEditResult WrongThread() => Failure("WRONG_THREAD",
        "Authoring mutations must run on the session's editor thread");

    private bool IsOwnerThread() => Environment.CurrentManagedThreadId == _ownerThreadId;

    private void EnsureOwnerThread()
    {
        if (!IsOwnerThread()) throw new InvalidOperationException(
            "Authoring mutations must run on the session's editor thread");
    }

    private static string Serialize(WorldDocument document) =>
        document.Serialize();

    private static string NormalizeId(string? id) => id?.Trim() ?? string.Empty;
    private static string Describe(string kind) => kind switch
    {
        "reparent" => "Reparent Entity",
        "transform" => "Transform Entity",
        _ => kind,
    };
    private static bool SameId(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    private static WorldLocalTransform CurrentTransform(WorldEntity entity) =>
        new(entity.Position, entity.Rotation, entity.Scale);

    private static bool IsValid(WorldLocalTransform transform) =>
        IsFinite(transform.Position) && IsFinite(transform.Rotation) && IsFinite(transform.Scale) &&
        transform.Scale.X > 0 && transform.Scale.Y > 0 && transform.Scale.Z > 0;

    private static void ApplyTransform(WorldEntity entity, WorldLocalTransform transform)
    {
        entity.Position = transform.Position;
        entity.Rotation = transform.Rotation;
        entity.Scale = transform.Scale;
    }

    private static Matrix4x4 Compose(WorldLocalTransform transform) =>
        Matrix4x4.CreateScale(transform.Scale.X, transform.Scale.Y, transform.Scale.Z) *
        Matrix4x4.CreateFromQuaternion(Quaternion.CreateFromYawPitchRoll(
            Degrees(transform.Rotation.Y), Degrees(transform.Rotation.X), Degrees(transform.Rotation.Z))) *
        Matrix4x4.CreateTranslation(transform.Position.X, transform.Position.Y, transform.Position.Z);

    private static WorldVector3 QuaternionToEuler(Quaternion value)
    {
        var sinPitch = 2 * (value.W * value.X - value.Y * value.Z);
        var pitch = MathF.Asin(Math.Clamp(sinPitch, -1, 1));
        var yaw = MathF.Atan2(2 * (value.W * value.Y + value.X * value.Z),
            1 - 2 * (value.X * value.X + value.Y * value.Y));
        var roll = MathF.Atan2(2 * (value.W * value.Z + value.X * value.Y),
            1 - 2 * (value.X * value.X + value.Z * value.Z));
        return new WorldVector3(Radians(pitch), Radians(yaw), Radians(roll));
    }

    private static bool NearlyEqual(Matrix4x4 left, Matrix4x4 right)
    {
        var a = new[] { left.M11, left.M12, left.M13, left.M14, left.M21, left.M22,
            left.M23, left.M24, left.M31, left.M32, left.M33, left.M34,
            left.M41, left.M42, left.M43, left.M44 };
        var b = new[] { right.M11, right.M12, right.M13, right.M14, right.M21, right.M22,
            right.M23, right.M24, right.M31, right.M32, right.M33, right.M34,
            right.M41, right.M42, right.M43, right.M44 };
        return a.Zip(b).All(pair => MathF.Abs(pair.First - pair.Second) <=
            MatrixTolerance * MathF.Max(1, MathF.Max(MathF.Abs(pair.First), MathF.Abs(pair.Second))));
    }

    private static bool IsFinite(WorldVector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    private static bool IsFinite(Quaternion value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z) && float.IsFinite(value.W);
    private static float Degrees(float value) => value * MathF.PI / 180;
    private static float Radians(float value) => value * 180 / MathF.PI;
    private static WorldVector3 ToWorld(Vector3 value) => new(value.X, value.Y, value.Z);

    private sealed record DocumentState(string Json);

    private sealed record HistoryEntry(Guid TransactionId, string Kind, string? TransactionGroupId,
        DocumentState Before, DocumentState After, ulong BeforeRevision, ulong AfterRevision,
        string[] AffectedEntityIds);

    private sealed record Destination(bool Success, string ParentId, WorldEntity? Anchor,
        string Code, string Message)
    {
        public static Destination Ok(string parentId, WorldEntity? anchor) =>
            new(true, parentId, anchor, string.Empty, string.Empty);
        public static Destination Fail(string code, string message) =>
            new(false, string.Empty, null, code, message);
    }
}
