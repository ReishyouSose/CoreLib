﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using CoreLib.Components;
using CoreLib.Submodules.ChatCommands;
using CoreLib.Submodules.DropTables;
using CoreLib.Submodules.ModEntity;
using CoreLib.Submodules.JsonLoader.Converters;
using CoreLib.Submodules.JsonLoader.Readers;
using CoreLib.Submodules.ModEntity.Atributes;
using CoreLib.Util.Extensions;
using System.Text.Json;
using System.Text.Json.Serialization;
using CoreLib.Scripts.Util.Extensions;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Serialization;

namespace CoreLib.Submodules.JsonLoader
{
    [CoreLibSubmodule(Dependencies = new[] { typeof(EntityModule), typeof(DropTablesModule) })]
    public class JsonLoaderModule
    {
        #region PUBLIC_INTERFACE

        /// <summary>
        /// Return true if the submodule is loaded.
        /// </summary>
        public static bool Loaded
        {
            get => _loaded;
            internal set => _loaded = value;
        }

        public static void UseConverter(params JsonConverter[] converters)
        {
            foreach (JsonConverter converter in converters)
            {
                if (options.Converters.All(jsonConverter => jsonConverter.GetType() != converter.GetType()))
                {
                    options.Converters.Add(converter);
                }
            }
        }

        public static IDisposable WithContext(JsonContext path)
        {
            return new ContextHandle(path);
        }

        public static void LoadFolder(string modGuid, string path)
        {
            ThrowIfNotLoaded();
            ThrowIfTooLate();

            if (modFolders.ContainsKey(modGuid))
            {
                CoreLibMod.Log.LogWarning($"Trying to load mod {modGuid} folder again!");
                return;
            }

            if (!Directory.Exists(Path.Combine(path, "resources")))
            {
                CoreLibMod.Log.LogWarning($"Mod {modGuid} folder does not contain 'resources' folder!");
                return;
            }

            string resourcesDir = Path.Combine(path, "resources");

            using (WithContext(new JsonContext(resourcesDir, Assembly.GetCallingAssembly())))
            {
                foreach (string file in Directory.EnumerateFiles(resourcesDir, "*.json", SearchOption.AllDirectories))
                {
                    string filename = file.Substring(file.LastIndexOf(Path.DirectorySeparatorChar) + 1);
                    JsonDocument document = JsonDocument.Parse(File.ReadAllText(file));
                    JsonElement jObject = document.RootElement;
                    FileContext fileContext = new FileContext(file, filename);

                    if (!jObject.TryGetProperty("type", out var typeElement))
                    {
                        CoreLibMod.Log.LogWarning(
                            $"JSON definition file {resourcesDir.GetRelativePath(file)} does not contain type information! Please specify 'type' value!");
                        continue;
                    }

                    string type = typeElement.GetString();

                    if (jsonReaders.ContainsKey(type))
                    {
                        try
                        {
                            CoreLibMod.Log.LogInfo($"Loading JSON file {filename} with {type} reader.");
                            IJsonReader reader = jsonReaders[type];
                            reader.ApplyPre(jObject, fileContext);
                            postApplyFiles.Add(file);
                        }
                        catch (Exception e)
                        {
                            CoreLibMod.Log.LogError($"Failed to add object:\n{e}");
                        }
                    }
                }

                modFolders.Add(modGuid, path);
            }
        }

        public static void RegisterJsonReaders(Assembly assembly)
        {
            IEnumerable<Type> types = assembly
                .GetTypes()
                .Where(type => type.GetCustomAttribute<RegisterReaderAttribute>() != null);

            foreach (Type type in types)
            {
                RegisterJsonReadersInType_Internal(type);
            }
        }

        public static int RegisterInteractHandler(string handlerType)
        {
            if (context.callingAssembly == null)
            {
                CoreLibMod.Log.LogError("Failed to register interaction handler. Context assembly is null");
                return 0;
            }

            Type type = context.callingAssembly.GetType(handlerType);
            if (type == null)
            {
                CoreLibMod.Log.LogError($"Failed to register interaction handler. Type '{handlerType}' not found!");
                return 0;
            }

            if (!typeof(IInteractionHandler).IsAssignableFrom(type) &&
                !typeof(ITriggerListener).IsAssignableFrom(type))
            {
                CoreLibMod.Log.LogError(
                    $"Failed to register interaction handler. Type {handlerType} does not implement '{nameof(IInteractionHandler)}' or '{nameof(ITriggerListener)}'!");
                return 0;
            }

            int existingMethod = interactionHandlers.FindIndex(info => info != null && info.GetType() == type);

            if (existingMethod > 0)
            {
                return existingMethod;
            }

            CoreLibMod.Log.LogDebug($"Registering {handlerType} as object interact handler!");
            int index = interactionHandlers.Count;
            interactionHandlers.Add(Activator.CreateInstance(type));
            return index;
        }

        public static Type TypeByName(string name)
        {
            Type type = Type.GetType(name, false);

            type ??= AllTypes().FirstOrDefault(t => t.FullName == name);
            type ??= AllTypes().FirstOrDefault(t => t.Name == name);

            if (type == null)
                CoreLibMod.Log.LogWarning($"Could not find type named {name}");
            return type;
        }

        public static void FillArrays<T>(T target)
        {
            FillArrays(typeof(T), target);
        }

        public static void FillArrays(Type type, object target)
        {
            foreach (PropertyInfo property in type.GetProperties())
            {
                if (!property.PropertyType.IsGenericType) continue;

                if (property.PropertyType.GetGenericTypeDefinition() == typeof(List<>))
                {
                    object value = property.GetValue(target);
                    if (value == null)
                    {
                        property.SetValue(target, Activator.CreateInstance(property.PropertyType));
                    }
                }
            }
        }

        public static void PopulateObject<T>(T target, JsonElement jsonSource)
        {
            PopulateObject(typeof(T), target, jsonSource, Array.Empty<string>());
        }

        public static void PopulateObject<T>(T target, JsonElement jsonSource, string[] exclude)
        {
            PopulateObject(typeof(T), target, jsonSource, exclude);
        }

        public static void PopulateObject(Type type, object target, JsonElement jsonSource)
        {
            PopulateObject(type, target, jsonSource, Array.Empty<string>());
        }

        public static void PopulateObject(Type type, object target, JsonElement jsonSource, string[] exclude)
        {
            foreach (JsonProperty property in jsonSource.EnumerateObject())
            {
                if (exclude.Contains(property.Name))
                {
                    CoreLibMod.Log.LogWarning($"Overriding {property.Name} is not allowed!");
                    continue;
                }

                try
                {
                    OverwriteProperty(type, target, property);
                }
                catch (Exception e)
                {
                    CoreLibMod.Log.LogWarning($"Failed to deserialize field/property '{property.Name}':\n{e}");
                }
            }
        }

        #endregion

        #region PRIVATE

        private static readonly string[] specialProperties =
        {
            "itemId",
            "requiredObjectsToCraft",
            "components",
            "localizedName",
            "localizedDescription",
            "colliderSize",
            "colliderCenter"
        };

        private static bool _loaded;
        private static bool dumpCommandEnabled;
        private static bool finishedLoadingObjects = false;
        private static bool entityModificationFileCacheReady = false;

        public static JsonSerializerOptions options;
        public static JsonContext context;

        internal static Dictionary<string, IJsonReader> jsonReaders = new Dictionary<string, IJsonReader>();
        internal static Dictionary<string, string> modFolders = new Dictionary<string, string>();
        internal static List<object> interactionHandlers = new List<object>(10);
        internal static List<ModifyFile> entityModificationFiles = new List<ModifyFile>();

        private static List<string> postApplyFiles = new List<string>();
        private static Dictionary<ObjectID, ModifyFile> entityModificationFileCache = new Dictionary<ObjectID, ModifyFile>();

        [CoreLibSubmoduleInit(Stage = InitStage.GetOptionalDependencies)]
        internal static Type[] GetOptionalDeps()
        {
            //dumpCommandEnabled = CoreLibMod.Config.Bind("Debug", "EnableDumpCommand", false, "Enable to allow object info to be dumped at runtime.").Value;

            if (dumpCommandEnabled)
            {
                return new[] { typeof(CommandsModule) };
            }

            return Array.Empty<Type>();
        }

        [CoreLibSubmoduleInit(Stage = InitStage.Load)]
        internal static void Load()
        {
            RegisterJsonReaders(Assembly.GetExecutingAssembly());

            options = new JsonSerializerOptions
            {
                IncludeFields = true,
                IgnoreReadOnlyProperties = true,
                WriteIndented = true,
                NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
            };
            options.Converters.Add(new ObjectTypeConverter());
            options.Converters.Add(new ObjectIDConverter());
            options.Converters.Add(new JsonStringEnumConverter());
            options.Converters.Add(new SpriteConverter());
            options.Converters.Add(new ColorConverter());
            options.Converters.Add(new VectorConverter());
            options.Converters.Add(new RectConverter());
            options.Converters.Add(new Texture2DConverter());
            options.Converters.Add(new LootTableIDConverter());

            // dummy converters
            options.Converters.Add(new IntPtrConverter());
            options.Converters.Add(new EntityMonoBehaviorDataConverter());
            options.Converters.Add(new GameObjectConverter());
            options.Converters.Add(new TransformConverter());
            options.Converters.Add(new EntityMonoBehaviorConverter());
            interactionHandlers.Add(null);
        }

        [CoreLibSubmoduleInit(Stage = InitStage.PostLoad)]
        internal static void PostLoad()
        {
            if (dumpCommandEnabled)
            {
                CommandsModule.RegisterCommandHandler(typeof(DumpCommandHandler), CoreLibMod.NAME);
            }

            EntityModule.RegisterEntityModifications(typeof(JsonLoaderModule));
        }

        internal static void PostApply()
        {
            CoreLibMod.Log.LogInfo("Start JSON post load");
            foreach (string file in postApplyFiles)
            {
                string filename = file.Substring(file.LastIndexOf(Path.DirectorySeparatorChar) + 1);
                var jObject = JsonDocument.Parse(File.ReadAllText(file)).RootElement;
                FileContext fileContext = new FileContext(file, filename);

                string type = jObject.GetProperty("type").GetString();

                if (jsonReaders.ContainsKey(type))
                {
                    try
                    {
                        CoreLibMod.Log.LogInfo($"Post Loading JSON file {filename} with {type} reader.");
                        IJsonReader reader = jsonReaders[type];
                        reader.ApplyPost(jObject, fileContext);
                    }
                    catch (Exception e)
                    {
                        CoreLibMod.Log.LogError($"Failed to post load:\n{e}");
                    }
                }
            }

            postApplyFiles.Clear();
        }

        //TODO update this API for new modification method
        [EntityModification]
        internal static void ModificationsApply(MonoBehaviour entity)
        {
            BuildModificationCache();

            var objectId = entity.gameObject.getEntityObjectID();

            if (entityModificationFileCache.ContainsKey(objectId))
            {
                ModifyFile modify = entityModificationFileCache[objectId];
                JsonDocument jObject = JsonDocument.Parse(File.ReadAllText(modify.filePath));

                using (WithContext(new JsonContext(modify.contextPath, null)))
                {
                    ModificationJsonReader.ModifyApply(jObject.RootElement, entity);
                }

                jObject.Dispose();
            }
        }

        private static void BuildModificationCache()
        {
            if (entityModificationFileCacheReady) return;

            foreach (ModifyFile modifyFile in entityModificationFiles)
            {
                ObjectID objectID = modifyFile.targetId.GetObjectID();
                if (objectID == ObjectID.None)
                {
                    CoreLibMod.Log.LogError($"Failed to apply entity modification, '{modifyFile.targetId}' is not a valid entity!");
                    continue;
                }

                entityModificationFileCache.Add(objectID, modifyFile);
            }

            entityModificationFileCacheReady = true;
            entityModificationFiles.Clear();
        }

        internal static void ThrowIfNotLoaded()
        {
            if (!Loaded)
            {
                Type submoduleType = MethodBase.GetCurrentMethod().DeclaringType;
                string message = $"{submoduleType.Name} is not loaded. Please use [{nameof(CoreLibSubmoduleDependency)}(nameof({submoduleType.Name})]";
                throw new InvalidOperationException(message);
            }
        }

        internal static void ThrowIfTooLate()
        {
            if (finishedLoadingObjects)
            {
                throw new InvalidOperationException("Json Loader finished loading items. Adding items at this stage is impossible!");
            }
        }

        internal static T GetInteractionHandler<T>(int index)
            where T : class
        {
            if (index <= 0)
            {
                throw new InvalidOperationException("Interaction handler is not valid!");
            }

            return interactionHandlers[index] as T;
        }

        private static void RegisterJsonReadersInType_Internal(Type type)
        {
            RegisterReaderAttribute attribute = type.GetCustomAttribute<RegisterReaderAttribute>();
            if (!jsonReaders.ContainsKey(attribute.typeName))
            {
                IJsonReader reader = Activator.CreateInstance(type) as IJsonReader;
                jsonReaders.Add(attribute.typeName, reader);
            }
            else
            {
                CoreLibMod.Log.LogError($"Failed to register {type.FullName} Json Reader, because name {attribute.typeName} is already taken!");
            }
        }

        private static void OverwriteProperty(Type type, object target, JsonProperty updatedProperty)
        {
            var propertyInfo = type.GetProperty(updatedProperty.Name);
            var fieldInfo = type.GetField(updatedProperty.Name);

            if (fieldInfo == null)
            {
                fieldInfo = type.GetFields()
                    .Where(field =>
                    {
                        var oldNameAttr = field.GetCustomAttribute<FormerlySerializedAsAttribute>();
                        return oldNameAttr != null && oldNameAttr.oldName.Equals(updatedProperty.Name);
                    }).FirstOrDefault();
            }

            if (propertyInfo == null && fieldInfo == null)
            {
                if (!specialProperties.Contains(updatedProperty.Name))
                    CoreLibMod.Log.LogWarning($"Property '{updatedProperty.Name}' not found!");
                return;
            }

            if (propertyInfo != null)
            {
                var parsedValue = updatedProperty.Value.Deserialize(propertyInfo.PropertyType, options);

                propertyInfo.SetValue(target, parsedValue);
            }
            else
            {
                var attribute = fieldInfo.GetCustomAttribute<NonSerializedAttribute>();
                if (attribute != null) return;

                var parsedValue = updatedProperty.Value.Deserialize(fieldInfo.FieldType, options);
                fieldInfo.SetValue(target, parsedValue);
            }
        }

        private static Type[] GetTypesFromAssembly(Assembly assembly)
        {
            try
            {
                return assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                return ex.Types.Where(type => type != null).ToArray();
            }
        }

        private static IEnumerable<Type> AllTypes() => AccessTools.AllAssemblies().SelectMany(GetTypesFromAssembly);

        #endregion
    }
}