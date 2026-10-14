using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using Autodesk.Revit.DB;

namespace GloryHoleRefreshElevations
{
    internal sealed class OpeningReplacementMetadata
    {
        private static readonly Guid PrimaryGuid = new Guid("652cfdd0-f868-4433-8fbd-16a517e1b878");
        private static readonly Guid[] Parameters = { PrimaryGuid,
            new Guid("8b5f2c01-9ad4-4e2f-b8c7-1d0e3a5b7c9d"), new Guid("9c603d12-0be5-5f3a-c9d8-2e1f4b6c8d0e"),
            new Guid("0d714e23-1cf6-6a4b-d0e9-3f2a5c7d9e1f"), new Guid("1e825f34-2da7-7b5c-e1fa-4a3b6d8e0f2a") };
        private static readonly HashSet<Guid> KnownSchemas = new HashSet<Guid> {
            new Guid("7fe4f25b-8156-45d4-956f-6cdccf518f70"), new Guid("a841b1f0-1c9a-4c78-80ce-6ca58a8ad564"),
            new Guid("05a963dc-c450-4bf7-b152-5ed10eae7335") };
        private readonly Dictionary<Guid, object> _values = new Dictionary<Guid, object>();
        private string _retainedSourceId;
        private readonly HashSet<Guid> _retainedSchemas = new HashSet<Guid>();
        private object _bridge;

        internal static OpeningReplacementMetadata Capture(FamilyInstance source) => Capture(source, CaptureBridge);
        internal static OpeningReplacementMetadata Capture(FamilyInstance source, bool replacing)
            => Capture(source, CaptureBridge, replacing);
        internal static OpeningReplacementMetadata Capture(FamilyInstance source, Func<FamilyInstance, object> captureBridge,
            bool replacing = true)
        {
            if (source == null || !source.IsValidObject) throw new InvalidOperationException("Исходное отверстие недоступно.");
            var snapshot = new OpeningReplacementMetadata();
            foreach (Guid id in Parameters)
            {
                Parameter parameter = source.get_Parameter(id);
                if (parameter == null || !parameter.HasValue) continue;
                object value = Read(parameter);
                snapshot._values.Add(id, value);
            }
            var schemas = source.GetEntitySchemaGuids();
            if (replacing && schemas.Any(id => !KnownSchemas.Contains(id)))
                throw new InvalidOperationException("У отверстия есть неизвестные служебные данные. Безопасный перенос не поддерживается.");
            // A direct level change does not delete the element or transfer its unknown data.
            // Keep that path tied to the original instance; known Cutter signatures still refresh.
            if (!replacing)
            {
                snapshot._retainedSourceId = source.UniqueId;
                snapshot._retainedSchemas.UnionWith(schemas.Where(id => !KnownSchemas.Contains(id)));
            }
            // GH_GUID alone is local identity. Do not even look up Cutter for an ordinary opening.
            if (schemas.Any(KnownSchemas.Contains) || snapshot._values.Any(pair => HasHubIdentity(pair.Key, pair.Value)))
            {
                snapshot._bridge = captureBridge(source);
                if (snapshot._bridge == null)
                    throw new InvalidOperationException("Не удалось подготовить перенос связей отверстия с заданиями и Hub.");
                snapshot.RequiredMethod("Apply"); snapshot.RequiredMethod("Verify");
            }
            return snapshot;
        }

        internal void Apply(FamilyInstance target)
        {
            VerifyRetainedInstance(target);
            foreach (var pair in _values)
            {
                Parameter parameter = target.get_Parameter(pair.Key);
                if (parameter == null || !MatchesType(parameter, pair.Value))
                    throw new InvalidOperationException("В новом отверстии отсутствует совместимый служебный параметр " + pair.Key + ".");
                if (parameter.HasValue && Equals(Read(parameter), pair.Value)) continue;
                if (parameter.IsReadOnly || !(pair.Value is string text ? parameter.Set(text) : parameter.Set((int)pair.Value)))
                    throw new InvalidOperationException("Не удалось перенести идентификатор или связь отверстия с Hub.");
            }
            if (_bridge != null) Invoke(RequiredMethod("Apply"), _bridge, target);
        }

        internal void Verify(FamilyInstance target)
        {
            VerifyRetainedInstance(target);
            foreach (var pair in _values)
            {
                Parameter parameter = target.get_Parameter(pair.Key);
                if (parameter == null || !parameter.HasValue || !MatchesType(parameter, pair.Value)
                    || !Equals(Read(parameter), pair.Value))
                    throw new InvalidOperationException("Служебные идентификаторы отверстия изменились при обработке Revit.");
            }
            if (_bridge != null) Invoke(RequiredMethod("Verify"), _bridge, target);
        }

        private void VerifyRetainedInstance(FamilyInstance target)
        {
            if (_retainedSourceId == null) return;
            if (target == null || !target.IsValidObject || target.UniqueId != _retainedSourceId
                || !_retainedSchemas.IsSubsetOf(target.GetEntitySchemaGuids()))
                throw new InvalidOperationException("При смене уровня изменился исходный экземпляр или его служебные данные.");
        }

        internal Action AfterCommit => _bridge == null ? null :
            (Action)Invoke(_bridge.GetType().GetProperty("AfterCommit")?.GetGetMethod(), _bridge);
        internal string Warning => _bridge == null ? null :
            (string)Invoke(_bridge.GetType().GetProperty("Warning")?.GetGetMethod(), _bridge);

        private MethodInfo RequiredMethod(string name) => _bridge.GetType().GetMethod(name, new[] { typeof(FamilyInstance) })
            ?? throw new InvalidOperationException("Версия GloryHoleCutter не поддерживает безопасный перенос связей отверстия.");

        private static object Read(Parameter parameter)
        {
            if (parameter.StorageType == StorageType.String) return parameter.AsString() ?? string.Empty;
            if (parameter.StorageType == StorageType.Integer) return parameter.AsInteger();
            throw new InvalidOperationException("Неподдерживаемый тип служебного параметра отверстия.");
        }
        private static bool MatchesType(Parameter parameter, object value) =>
            parameter.StorageType == (value is string ? StorageType.String : StorageType.Integer);

        private static bool HasHubIdentity(Guid id, object value)
        {
            if (id == PrimaryGuid) return false;
            if (id == Parameters[1])
                return value is string project && Guid.TryParse(project, out Guid parsed) && parsed != Guid.Empty;
            return value is int number ? number > 0 : int.TryParse(value as string,
                NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedNumber) && parsedNumber > 0;
        }

        private static object CaptureBridge(FamilyInstance source)
        {
            const string assemblyName = "GloryHoleCutter";
            Assembly assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == assemblyName);
            if (assembly == null)
            {
                string folder = Path.GetDirectoryName(typeof(OpeningReplacementMetadata).Assembly.Location);
                string candidate = Path.GetFullPath(Path.Combine(folder, "..", assemblyName, assemblyName + ".dll"));
                if (!File.Exists(candidate)) candidate = Path.Combine(folder, assemblyName + ".dll");
                if (!File.Exists(candidate))
                    throw new InvalidOperationException("Для переноса служебных связей нужен установленный GloryHoleCutter соответствующей версии Revit.");
                assembly = Assembly.LoadFrom(candidate);
            }
            AssemblyName api = assembly.GetReferencedAssemblies().FirstOrDefault(a => a.Name == "RevitAPI");
            if (api == null || api.Version.Major != typeof(Document).Assembly.GetName().Version.Major)
                throw new InvalidOperationException("GloryHoleCutter собран для другой версии Revit; перенос связей отменён.");
            Type bridge = assembly.GetType("GloryHoleCutter.OpeningReplacementBridge", false);
            MethodInfo capture = bridge?.GetMethod("Capture", BindingFlags.Public | BindingFlags.Static, null,
                new[] { typeof(FamilyInstance) }, null);
            if (capture == null)
                throw new InvalidOperationException("Обновите GloryHoleCutter: установленная версия не поддерживает перенос связей отверстия.");
            return Invoke(capture, null, source);
        }

        private static object Invoke(MethodInfo method, object instance, params object[] arguments)
        {
            if (method == null) return null;
            try { return method.Invoke(instance, arguments); }
            catch (TargetInvocationException ex) when (ex.InnerException != null)
            {
                ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                throw;
            }
        }
    }
}
