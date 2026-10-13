// Test doubles for the production service's Revit boundary. These model parameter-driven
// placement and transaction rollback, not Revit's geometric or failure-solving engine.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace Autodesk.Revit.Exceptions
{
    public class RegenerationFailedException : Exception
    {
        public RegenerationFailedException(string message) : base(message) { }
    }
}

namespace Autodesk.Revit.DB
{
    public enum StorageType { None, Integer, Double, String, ElementId }
    public enum BuiltInCategory { OST_GenericModel = -2000151, OST_Windows = -2000014, OST_Walls = -2000011, OST_Floors = -2000032 }
    public enum FamilyPlacementType { OneLevelBased, OneLevelBasedHosted, WorkPlaneBased, TwoLevelsBased, Adaptive }
    public enum BuiltInParameter
    {
        FAMILY_LEVEL_PARAM, INSTANCE_ELEVATION_PARAM, INSTANCE_FREE_HOST_OFFSET_PARAM,
        INSTANCE_SILL_HEIGHT_PARAM, FLOOR_HEIGHTABOVELEVEL_PARAM,
        FAMILY_WIDTH_PARAM, FAMILY_HEIGHT_PARAM, WINDOW_WIDTH, WINDOW_HEIGHT,
        GENERIC_WIDTH, GENERIC_HEIGHT, DOOR_WIDTH, DOOR_HEIGHT, CASEWORK_WIDTH, CASEWORK_HEIGHT
    }
    public enum TransactionStatus { Uninitialized, Started, Committed, RolledBack, Pending, Error }
    public enum FailureSeverity { None, Warning, Error, DocumentCorruption }
    public enum FailureProcessingResult { Continue, ProceedWithRollBack, ProceedWithCommit }

    public sealed class ElementId : IEquatable<ElementId>
    {
        public ElementId(int value) { Value = value; }
        public ElementId(long value) { Value = value; }
        public ElementId(BuiltInCategory value) { Value = (long)value; }
        public long Value { get; }
        public int IntegerValue => (int)Value;
        public static ElementId InvalidElementId { get; } = new ElementId(-1);
        public bool Equals(ElementId other) => other != null && Value == other.Value;
        public override bool Equals(object obj) => Equals(obj as ElementId);
        public override int GetHashCode() => Value.GetHashCode();
        public override string ToString() => Value.ToString();
    }

    public sealed class XYZ
    {
        public XYZ(double x, double y, double z) { X = x; Y = y; Z = z; }
        public double X { get; }
        public double Y { get; }
        public double Z { get; }
        public static XYZ Zero => new XYZ(0, 0, 0);
        public static XYZ BasisX => new XYZ(1, 0, 0);
        public static XYZ BasisY => new XYZ(0, 1, 0);
        public static XYZ BasisZ => new XYZ(0, 0, 1);
        public static XYZ operator +(XYZ a, XYZ b) => new XYZ(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static XYZ operator -(XYZ a, XYZ b) => new XYZ(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public double DistanceTo(XYZ other) => Math.Sqrt(Math.Pow(X-other.X, 2) + Math.Pow(Y-other.Y, 2) + Math.Pow(Z-other.Z, 2));
        public bool IsAlmostEqualTo(XYZ other) => DistanceTo(other) < 1e-9;
    }
    public sealed class Transform
    {
        public static Transform Identity => new Transform();
        public XYZ Origin { get; set; } = XYZ.Zero;
        public XYZ BasisX { get; set; } = XYZ.BasisX;
        public XYZ BasisY { get; set; } = XYZ.BasisY;
        public XYZ BasisZ { get; set; } = XYZ.BasisZ;
        internal Transform Copy() => new Transform { Origin = Origin, BasisX = BasisX, BasisY = BasisY, BasisZ = BasisZ };
    }
    public abstract class Location { }
    public sealed class LocationPoint : Location
    {
        public XYZ Point { get; set; } = XYZ.Zero;
        public double Rotation { get; set; }
    }
    public sealed class LocationCurve : Location { }
    public sealed class Category { public ElementId Id { get; set; } }
    public sealed class Reference
    {
        public Reference(string stable) { Stable = stable; }
        public string Stable { get; }
        public string ConvertToStableRepresentation(Document doc) => Stable;
    }
    public sealed class Parameter
    {
        internal object Value;
        public Parameter(StorageType storageType, object value) { StorageType = storageType; Value = value; }
        public StorageType StorageType { get; }
        public bool IsReadOnly { get; set; }
        public bool HasValue => Value != null;
        public bool RejectSet { get; set; }
        public Action<object> AfterSet { get; set; }
        public double AsDouble() => Convert.ToDouble(Value);
        public int AsInteger() => Convert.ToInt32(Value);
        public string AsString() => Value as string;
        public ElementId AsElementId() => (ElementId)Value;
        public bool Set(double value) => SetValue(value);
        public bool Set(int value) => SetValue(value);
        public bool Set(string value) => SetValue(value);
        public bool Set(ElementId value) => SetValue(value);
        private bool SetValue(object value)
        {
            if (IsReadOnly) throw new InvalidOperationException("Parameter is read only");
            if (RejectSet) return false;
            Value = value;
            AfterSet?.Invoke(value);
            return true;
        }
    }
    public class Element
    {
        private readonly Dictionary<object, Parameter> _parameters = new Dictionary<object, Parameter>();
        public ElementId Id { get; set; }
        public string UniqueId { get; set; }
        public string Name { get; set; } = "element";
        public Document Document { get; internal set; }
        public bool IsValidObject { get; set; } = true;
        public bool Pinned { get; set; }
        public Category Category { get; set; }
        public ElementId LevelId { get; set; } = ElementId.InvalidElementId;
        public ElementId GroupId { get; set; } = ElementId.InvalidElementId;
        public Location Location { get; set; }
        public Parameter get_Parameter(Guid id) => _parameters.TryGetValue(id, out var value) ? value : null;
        public Parameter get_Parameter(BuiltInParameter id) => _parameters.TryGetValue(id, out var value) ? value : null;
        public void Put(object key, Parameter parameter)
        {
            if (parameter == null) _parameters.Remove(key);
            else _parameters[key] = parameter;
        }
        public virtual ElementId GetTypeId() => ElementId.InvalidElementId;
        internal virtual Action Capture()
        {
            var values = _parameters.ToDictionary(pair => pair.Key, pair => (pair.Value, pair.Value.Value));
            ElementId level = LevelId, group = GroupId;
            bool valid = IsValidObject, pinned = Pinned;
            string uniqueId = UniqueId;
            var location = Location;
            XYZ point = (Location as LocationPoint)?.Point;
            double rotation = (Location as LocationPoint)?.Rotation ?? 0;
            return () =>
            {
                _parameters.Clear();
                foreach (var pair in values) { _parameters[pair.Key] = pair.Value.Item1; pair.Value.Item1.Value = pair.Value.Item2; }
                LevelId = level; GroupId = group; IsValidObject = valid; Pinned = pinned; UniqueId = uniqueId; Location = location;
                if (Location is LocationPoint lp) { lp.Point = point; lp.Rotation = rotation; }
            };
        }
    }
    public sealed class Level : Element
    {
        public double ProjectElevation { get; set; }
        public double Elevation { get; set; }
    }
    public sealed class Family : Element { public FamilyPlacementType FamilyPlacementType { get; set; } }
    public sealed class FamilySymbol : Element
    {
        public Family Family { get; set; }
        public string FamilyName => Family.Name;
    }
    public sealed class FamilyInstance : Element
    {
        public FamilySymbol Symbol { get; set; }
        public Element Host { get; set; }
        public Reference HostFace { get; set; }
        public Element SuperComponent { get; set; }
        public bool Mirrored { get; set; }
        public bool HandFlipped { get; set; }
        public bool FacingFlipped { get; set; }
        public bool IsVoidCutting { get; set; } = true;
        public Transform Orientation { get; set; } = Transform.Identity;
        public Transform GetTransform()
        {
            Transform result = Orientation.Copy();
            result.Origin = ((LocationPoint)Location).Point + Orientation.Origin;
            return result;
        }
        public override ElementId GetTypeId() => Symbol.Id;
        internal override Action Capture()
        {
            Action restoreBase = base.Capture();
            var host = Host; var face = HostFace; var symbol = Symbol; var parent = SuperComponent;
            var orientation = Orientation.Copy();
            bool mirrored = Mirrored, hand = HandFlipped, facing = FacingFlipped, voidCutting = IsVoidCutting;
            return () => { restoreBase(); Host = host; HostFace = face; Symbol = symbol; SuperComponent = parent;
                Orientation = orientation.Copy(); Mirrored = mirrored; HandFlipped = hand; FacingFlipped = facing; IsVoidCutting = voidCutting; };
        }
    }
    public class HostObject : Element
    {
        public HashSet<ElementId> Inserts { get; } = new HashSet<ElementId>();
        public ICollection<ElementId> FindInserts(bool a, bool b, bool c, bool d) => Inserts.ToList();
        internal override Action Capture()
        {
            Action restoreBase = base.Capture(); var inserts = Inserts.ToList();
            return () => { restoreBase(); Inserts.Clear(); Inserts.UnionWith(inserts); };
        }
    }
    public sealed class Wall : HostObject { }
    public sealed class Floor : HostObject { }
    public sealed class Document
    {
        internal readonly List<Element> Elements = new List<Element>();
        internal readonly Dictionary<ElementId, HashSet<ElementId>> VoidCuts = new Dictionary<ElementId, HashSet<ElementId>>();
        internal readonly List<(ElementId A, ElementId B, ElementId Cutter)> Joins = new List<(ElementId, ElementId, ElementId)>();
        public Action<string> AfterCommit { get; set; }
        public Action OnRegenerate { get; set; }
        public List<FailureMessageAccessor> NextFailures { get; } = new List<FailureMessageAccessor>();
        public bool IsModifiable { get; internal set; }
        public bool IsReadOnly { get; set; }
        public bool IsFamilyDocument { get; set; }
        public bool IsLinked { get; set; }
        public bool IsValidObject => true;
        public int ReadsAfterFatalRegeneration { get; private set; }
        private bool _fatal;
        public T Add<T>(T element) where T : Element
        {
            element.Document = this;
            if (element.Id == null) element.Id = new ElementId(Elements.Count + 1);
            if (element.UniqueId == null) element.UniqueId = "id-" + element.Id;
            Elements.Add(element);
            return element;
        }
        public Element GetElement(ElementId id) { GuardRead(); return Elements.FirstOrDefault(e => e.Id.Equals(id) && e.IsValidObject); }
        public Element GetElement(string id) { GuardRead(); return Elements.FirstOrDefault(e => e.UniqueId == id && e.IsValidObject); }
        public void Regenerate()
        {
            GuardRead();
            try { OnRegenerate?.Invoke(); }
            catch (Autodesk.Revit.Exceptions.RegenerationFailedException) { _fatal = true; throw; }
        }
        internal void GuardRead()
        {
            if (_fatal) { ReadsAfterFatalRegeneration++; throw new InvalidOperationException("Document read after fatal regeneration"); }
        }
        internal Action Capture()
        {
            var elements = Elements.ToArray(); var restores = elements.Select(e => e.Capture()).ToArray();
            var cuts = VoidCuts.ToDictionary(pair => pair.Key, pair => pair.Value.ToList()); var joins = Joins.ToArray();
            return () =>
            {
                Elements.Clear(); Elements.AddRange(elements);
                foreach (Action restore in restores) restore();
                VoidCuts.Clear(); foreach (var cut in cuts) VoidCuts[cut.Key] = new HashSet<ElementId>(cut.Value);
                Joins.Clear(); Joins.AddRange(joins);
            };
        }
    }
    public sealed class FilteredElementCollector : IEnumerable<Element>
    {
        private readonly Document _doc;
        private Type _type = typeof(Element);
        public FilteredElementCollector(Document doc) { _doc = doc; }
        public FilteredElementCollector OfClass(Type type) { _type = type; return this; }
        public IEnumerator<Element> GetEnumerator() { _doc.GuardRead(); return _doc.Elements.Where(e => _type.IsInstanceOfType(e)).GetEnumerator(); }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
    public interface IFailuresPreprocessor { FailureProcessingResult PreprocessFailures(FailuresAccessor accessor); }
    public sealed class FailureMessageAccessor
    {
        public FailureSeverity Severity { get; set; }
        public string Description { get; set; } = "Revit failure";
        public FailureSeverity GetSeverity() => Severity;
        public string GetDescriptionText() => Description;
    }
    public sealed class FailuresAccessor
    {
        private readonly IList<FailureMessageAccessor> _messages;
        public FailuresAccessor(IList<FailureMessageAccessor> messages) { _messages = messages; }
        public IList<FailureMessageAccessor> GetFailureMessages() => _messages;
    }
    public sealed class FailureHandlingOptions
    {
        internal IFailuresPreprocessor Preprocessor;
        public FailureHandlingOptions SetFailuresPreprocessor(IFailuresPreprocessor value) { Preprocessor = value; return this; }
        public FailureHandlingOptions SetClearAfterRollback(bool value) => this;
        public FailureHandlingOptions SetForcedModalHandling(bool value) => this;
    }
    public sealed class Transaction : IDisposable
    {
        private readonly Document _doc; private readonly string _name;
        private Action _restore; private TransactionStatus _status;
        private FailureHandlingOptions _options = new FailureHandlingOptions();
        public Transaction(Document doc, string name) { _doc = doc; _name = name; }
        public TransactionStatus Start() { _restore = _doc.Capture(); _doc.IsModifiable = true; return _status = TransactionStatus.Started; }
        public TransactionStatus GetStatus() => _status;
        public FailureHandlingOptions GetFailureHandlingOptions() => _options;
        public void SetFailureHandlingOptions(FailureHandlingOptions options) { _options = options; }
        public TransactionStatus Commit()
        {
            var failures = _doc.NextFailures.ToList(); _doc.NextFailures.Clear();
            if (_options.Preprocessor != null && _options.Preprocessor.PreprocessFailures(new FailuresAccessor(failures)) == FailureProcessingResult.ProceedWithRollBack)
                return RollBack();
            _status = TransactionStatus.Committed; _doc.IsModifiable = false;
            _doc.AfterCommit?.Invoke(_name);
            return _status;
        }
        public TransactionStatus RollBack() { _restore(); _doc.IsModifiable = false; return _status = TransactionStatus.RolledBack; }
        public void Dispose() { if (_status == TransactionStatus.Started) RollBack(); }
    }
    public sealed class TransactionGroup : IDisposable
    {
        private readonly Document _doc; private Action _restore; private TransactionStatus _status;
        public TransactionGroup(Document doc, string name) { _doc = doc; }
        public TransactionStatus Start() { _restore = _doc.Capture(); return _status = TransactionStatus.Started; }
        public TransactionStatus GetStatus() => _status;
        public TransactionStatus Assimilate() => _status = TransactionStatus.Committed;
        public TransactionStatus RollBack() { _restore(); return _status = TransactionStatus.RolledBack; }
        public void Dispose() { if (_status == TransactionStatus.Started) RollBack(); }
    }
    public static class InstanceVoidCutUtils
    {
        public static ICollection<ElementId> GetElementsBeingCut(FamilyInstance instance)
        {
            if (!instance.IsVoidCutting) throw new ArgumentException("Instance has no unattached void");
            return instance.Document.VoidCuts.TryGetValue(instance.Id, out var ids) ? ids.ToList() : new List<ElementId>();
        }
        public static bool InstanceVoidCutExists(Element host, Element instance)
            => instance.Document.VoidCuts.TryGetValue(instance.Id, out var ids) && ids.Contains(host.Id);
        public static bool IsVoidInstanceCuttingElement(Element instance) => instance is FamilyInstance family && family.IsVoidCutting;
    }
    public static class JoinGeometryUtils
    {
        public static ICollection<ElementId> GetJoinedElements(Document doc, Element instance)
            => doc.Joins.Where(j => j.A.Equals(instance.Id) || j.B.Equals(instance.Id)).Select(j => j.A.Equals(instance.Id) ? j.B : j.A).ToList();
        public static bool AreElementsJoined(Document doc, Element first, Element second)
            => doc.Joins.Any(j => (j.A.Equals(first.Id) && j.B.Equals(second.Id)) || (j.A.Equals(second.Id) && j.B.Equals(first.Id)));
        public static bool IsCuttingElementInJoin(Document doc, Element first, Element second)
            => doc.Joins.Any(j => j.Cutter.Equals(first.Id) && ((j.A.Equals(first.Id) && j.B.Equals(second.Id)) || (j.A.Equals(second.Id) && j.B.Equals(first.Id))));
    }
    public sealed class AddInId { public AddInId(Guid id) { } }
    public sealed class UpdaterId { public UpdaterId(AddInId appId, Guid id) { } }
    public static class UpdaterRegistry
    {
        public static bool Registered = true, Enabled = true;
        public static bool IsUpdaterRegistered(UpdaterId id) => Registered;
        public static bool IsUpdaterEnabled(UpdaterId id) => Enabled;
        public static void DisableUpdater(UpdaterId id) { Enabled = false; }
        public static void EnableUpdater(UpdaterId id) { Enabled = true; }
    }
}
