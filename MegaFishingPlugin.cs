using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace MegaFishing
{
    [BepInPlugin(PluginGUID, PluginName, PluginVersion)]
    public class MegaFishingPlugin : BaseUnityPlugin
    {
        private const string PluginGUID = "com.rikmods.megafishing";
        private const string PluginName = "Mega Fishing";
        private const string PluginVersion = "1.1.2";

        private ConfigEntry<bool> _modEnabled;
        private ConfigEntry<float> _pullRadius;
        internal ConfigEntry<int> _fishLevelIncrease;
        private ConfigEntry<float> _pullInterval;
        private ConfigEntry<bool> _pullToPlayer;
        public static ConfigEntry<bool> DebugMode;

        /// <summary>Gated diagnostic log. Silent unless DebugMode = true.</summary>
        public static void Log(string msg) { if (DebugMode?.Value == true) Instance?.Logger.LogInfo(msg); }
        /// <summary>Unconditional log — reserved for milestones and genuine errors.</summary>
        public static void LogAlways(string msg) => Instance?.Logger.LogInfo(msg);

        private float _timer;
        private FileSystemWatcher _configWatcher;
        private bool _configDirty;

        internal static MegaFishingPlugin Instance;

        private void Awake()
        {
            MigrateConfig(Config.ConfigFilePath);
            Config.Reload();

            _modEnabled = Config.Bind("1. General", "Enabled", true,
                "Enable or disable the MegaFishing mod.");

            _pullRadius = Config.Bind("1. General", "PullRadius", 20f,
                new ConfigDescription(
                    "Radius (in meters) within which fish items are pulled into containers.",
                    new AcceptableValueRange<float>(1f, 100f)));

            _fishLevelIncrease = Config.Bind("1. General", "FishLevelIncrease", 0,
                new ConfigDescription(
                    "Quality levels to add to fish when pulled into a container. " +
                    "0 = no upgrade, 4 = maximum (+4 levels, e.g. level 1 becomes level 5).",
                    new AcceptableValueRange<int>(0, 4)));

            _pullInterval = Config.Bind("1. General", "PullIntervalSeconds", 5f,
                new ConfigDescription(
                    "How often (in seconds) containers scan for nearby fish to pull in.",
                    new AcceptableValueRange<float>(1f, 60f)));

            _pullToPlayer = Config.Bind("1. General", "PullToPlayer", false,
                "When enabled, fish on the ground near the player are also pulled " +
                "into the player's inventory (same radius / level-upgrade rules apply).");

            DebugMode = Config.Bind("99. Debug", "DebugMode", false,
                "Enable verbose debug logging to BepInEx console/log");

            SetupConfigWatcher();

            Instance = this;
            new Harmony(PluginGUID).PatchAll();

            Logger.LogInfo($"{PluginName} v{PluginVersion} loaded.");
        }

        private void OnDestroy()
        {
            if (_configWatcher != null)
            {
                _configWatcher.Changed -= OnConfigFileChanged;
                _configWatcher.Dispose();
                _configWatcher = null;
            }
        }

        private void SetupConfigWatcher()
        {
            string configPath = Config.ConfigFilePath;
            string directory = Path.GetDirectoryName(configPath);
            string fileName = Path.GetFileName(configPath);

            _configWatcher = new FileSystemWatcher(directory, fileName)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
                EnableRaisingEvents = true
            };
            _configWatcher.Changed += OnConfigFileChanged;
        }

        private void OnConfigFileChanged(object sender, FileSystemEventArgs e)
        {
            // Flag for reload on the main thread (FileSystemWatcher fires on a background thread).
            _configDirty = true;
        }

        private void Update()
        {
            if (_configDirty)
            {
                _configDirty = false;
                Config.Reload();
                Logger.LogInfo("Configuration reloaded.");
            }

            if (!_modEnabled.Value || Player.m_localPlayer == null)
                return;

            _timer += Time.deltaTime;
            if (_timer < _pullInterval.Value)
                return;
            _timer = 0f;

            HashSet<ItemDrop> consumed = new HashSet<ItemDrop>();
            ItemDrop[] allDrops = FindObjectsByType<ItemDrop>(FindObjectsSortMode.None);
            PullFishIntoContainers(consumed, allDrops);

            if (_pullToPlayer.Value)
                PullFishIntoPlayerInventory(consumed, allDrops);
        }

        private void PullFishIntoContainers(HashSet<ItemDrop> consumed, ItemDrop[] drops)
        {
            float radiusSq = _pullRadius.Value * _pullRadius.Value;
            int levelIncrease = _fishLevelIncrease.Value;

            Container[] containers = FindObjectsByType<Container>(FindObjectsSortMode.None);
            if (containers.Length == 0)
                return;

            if (drops.Length == 0)
                return;

            foreach (Container container in containers)
            {
                if (container == null)
                    continue;

                ZNetView containerView = container.GetComponent<ZNetView>();
                if (containerView == null || !containerView.IsValid())
                    continue;

                // Only the ZDO owner may modify the container (avoids desync).
                if (!containerView.IsOwner())
                    continue;

                Inventory inventory = container.GetInventory();
                if (inventory == null)
                    continue;

                // Collect the shared-name tokens of fish already stored in this container.
                // Also passively repair any fish whose m_dropPrefab was lost — those items
                // would otherwise NRE inside Smelter.IsItemAllowed at the Preparation Table.
                HashSet<string> fishNames = new HashSet<string>(StringComparer.Ordinal);
                foreach (ItemDrop.ItemData item in inventory.GetAllItems())
                {
                    if (item?.m_shared == null || !IsFishName(item.m_shared.m_name))
                        continue;
                    fishNames.Add(item.m_shared.m_name);
                    RepairDropPrefab(item);
                }

                if (fishNames.Count == 0)
                    continue;

                Vector3 pos = container.transform.position;

                foreach (ItemDrop drop in drops)
                {
                    if (consumed.Contains(drop))
                        continue;

                    if (drop == null || drop.m_itemData?.m_shared == null)
                        continue;

                    ZNetView dropView = drop.GetComponent<ZNetView>();
                    if (dropView == null || !dropView.IsValid())
                        continue;

                    // Squared-distance check (avoids sqrt).
                    if ((drop.transform.position - pos).sqrMagnitude > radiusSq)
                        continue;

                    // Must match one of the fish types already in this container.
                    if (!fishNames.Contains(drop.m_itemData.m_shared.m_name))
                        continue;

                    // Apply quality / level upgrade before adding.
                    if (levelIncrease > 0)
                    {
                        int max = drop.m_itemData.m_shared.m_maxQuality > 0
                            ? drop.m_itemData.m_shared.m_maxQuality
                            : 5;
                        drop.m_itemData.m_quality = Mathf.Min(
                            drop.m_itemData.m_quality + levelIncrease, max);
                    }

                    // Inventory.AddItem(ItemData) does not set m_dropPrefab. Smelter
                    // (Preparation Table, Cooking Station, etc.) dereferences
                    // item.m_dropPrefab.name, so pulling a drop with a null prefab
                    // poisons the container — the recipe silently refuses.
                    if (!EnsureDropPrefab(drop))
                        continue;

                    // Only add if the container has room.
                    if (!inventory.CanAddItem(drop.m_itemData))
                        continue;

                    inventory.AddItem(drop.m_itemData);

                    // Remove the world object.
                    dropView.ClaimOwnership();
                    dropView.Destroy();
                    consumed.Add(drop);
                }
            }
        }

        private void PullFishIntoPlayerInventory(HashSet<ItemDrop> consumed, ItemDrop[] drops)
        {
            Player player = Player.m_localPlayer;
            if (player == null)
                return;

            float radiusSq = _pullRadius.Value * _pullRadius.Value;
            int levelIncrease = _fishLevelIncrease.Value;

            Inventory inventory = player.GetInventory();
            if (inventory == null)
                return;

            // Collect the fish types the player is already carrying.
            // Also passively repair any fish whose m_dropPrefab was lost — those items
            // would otherwise NRE inside Smelter.IsItemAllowed at the Preparation Table.
            HashSet<string> fishNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (ItemDrop.ItemData item in inventory.GetAllItems())
            {
                if (item?.m_shared == null || !IsFishName(item.m_shared.m_name))
                    continue;
                fishNames.Add(item.m_shared.m_name);
                RepairDropPrefab(item);
            }

            if (fishNames.Count == 0)
                return;

            Vector3 pos = player.transform.position;

            foreach (ItemDrop drop in drops)
            {
                if (consumed.Contains(drop))
                    continue;

                if (drop == null || drop.m_itemData?.m_shared == null)
                    continue;

                ZNetView dropView = drop.GetComponent<ZNetView>();
                if (dropView == null || !dropView.IsValid())
                    continue;

                if ((drop.transform.position - pos).sqrMagnitude > radiusSq)
                    continue;

                if (!fishNames.Contains(drop.m_itemData.m_shared.m_name))
                    continue;

                if (levelIncrease > 0)
                {
                    int max = drop.m_itemData.m_shared.m_maxQuality > 0
                        ? drop.m_itemData.m_shared.m_maxQuality
                        : 5;
                    drop.m_itemData.m_quality = Mathf.Min(
                        drop.m_itemData.m_quality + levelIncrease, max);
                }

                if (!EnsureDropPrefab(drop))
                    continue;

                if (!inventory.CanAddItem(drop.m_itemData))
                    continue;

                inventory.AddItem(drop.m_itemData);

                dropView.ClaimOwnership();
                dropView.Destroy();
                consumed.Add(drop);
            }
        }

        /// <summary>
        /// Ensures the given drop's ItemData has a non-null m_dropPrefab before it's
        /// handed to Inventory.AddItem. The AddItem(ItemData) overload does not set
        /// m_dropPrefab itself, and Smelter.IsItemAllowed dereferences it — so any
        /// ground drop lacking the prefab reference becomes invisible to the
        /// Preparation Table / Cooking Station once stored. Returns false when the
        /// prefab cannot be resolved, in which case the caller should skip the pull.
        /// </summary>
        private static bool EnsureDropPrefab(ItemDrop drop)
        {
            if (drop?.m_itemData?.m_shared == null)
                return false;

            if (drop.m_itemData.m_dropPrefab != null)
                return true;

            GameObject prefab = ResolvePrefabBySharedName(drop.m_itemData.m_shared.m_name);
            if (prefab == null)
            {
                Log($"Skipping pull: no prefab found for {drop.m_itemData.m_shared.m_name}");
                return false;
            }

            drop.m_itemData.m_dropPrefab = prefab;
            Log($"Resolved missing m_dropPrefab for {drop.m_itemData.m_shared.m_name} -> {prefab.name}");
            return true;
        }

        /// <summary>
        /// In-place repair for items already living in an inventory with a null
        /// m_dropPrefab. Runs as part of every pull scan so cursed fish heal
        /// passively without needing a dedicated one-shot hook.
        /// </summary>
        private static void RepairDropPrefab(ItemDrop.ItemData item)
        {
            if (item?.m_shared == null || item.m_dropPrefab != null)
                return;

            GameObject prefab = ResolvePrefabBySharedName(item.m_shared.m_name);
            if (prefab == null)
                return;

            item.m_dropPrefab = prefab;
            Log($"Repaired null m_dropPrefab on stored {item.m_shared.m_name} -> {prefab.name}");
        }

        /// <summary>
        /// Finds the prefab GameObject in ObjectDB whose ItemDrop carries the given
        /// shared-name token (e.g. "$item_fish1"). Returns null when ObjectDB is not
        /// yet initialised or no match exists.
        /// </summary>
        private static GameObject ResolvePrefabBySharedName(string sharedName)
        {
            ObjectDB db = ObjectDB.instance;
            if (db == null || db.m_items == null || string.IsNullOrEmpty(sharedName))
                return null;

            for (int i = 0; i < db.m_items.Count; i++)
            {
                GameObject go = db.m_items[i];
                if (go == null)
                    continue;
                ItemDrop drop = go.GetComponent<ItemDrop>();
                if (drop?.m_itemData?.m_shared != null
                    && drop.m_itemData.m_shared.m_name == sharedName)
                    return go;
            }
            return null;
        }

        /// <summary>
        /// Returns true when the shared-name token matches the Valheim fish naming
        /// convention — contains "fish" immediately followed by a digit (e.g.
        /// "$item_fish1", "$item_fish12"). This correctly excludes unrelated items
        /// such as FishingRod or FishingBait.
        /// </summary>
        internal static bool IsFishName(string sharedName)
        {
            if (string.IsNullOrEmpty(sharedName))
                return false;

            int idx = sharedName.IndexOf("fish", StringComparison.OrdinalIgnoreCase);
            while (idx >= 0)
            {
                int next = idx + 4;
                if (next < sharedName.Length && char.IsDigit(sharedName[next]))
                    return true;

                idx = sharedName.IndexOf("fish", next, StringComparison.OrdinalIgnoreCase);
            }

            return false;
        }

        private static void MigrateConfig(string configPath)
        {
            try
            {
                if (!File.Exists(configPath)) return;
                string text = File.ReadAllText(configPath);
                bool changed = false;

                changed |= MigrateCfgSection(ref text, "General", "1. General");
                changed |= MigrateCfgSection(ref text, "Debug", "99. Debug");
                changed |= MigrateCfgSection(ref text, "2. Debug", "99. Debug");

                if (changed)
                    File.WriteAllText(configPath, text.TrimEnd() + "\n");
            }
            catch { }
        }

        private static bool MigrateCfgSection(ref string text, string oldName, string newName)
        {
            string oldHeader = "[" + oldName + "]";
            int idx = text.IndexOf(oldHeader, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return false;

            int sectionEnd = text.IndexOf("\n[", idx + oldHeader.Length, StringComparison.Ordinal);

            if (newName == null || text.IndexOf("[" + newName + "]", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                if (sectionEnd < 0)
                    text = text.Substring(0, idx).TrimEnd('\r', '\n');
                else
                    text = text.Substring(0, idx) + text.Substring(sectionEnd + 1);
            }
            else
            {
                text = text.Remove(idx, oldHeader.Length).Insert(idx, "[" + newName + "]");
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(FishingFloat), nameof(FishingFloat.Catch))]
    static class FishingFloat_Catch_Patch
    {
        static void Prefix(Fish fish)
        {
            if (fish == null || MegaFishingPlugin.Instance == null)
                return;

            int levelIncrease = MegaFishingPlugin.Instance._fishLevelIncrease.Value;
            if (levelIncrease <= 0)
                return;

            ItemDrop itemDrop = fish.gameObject.GetComponent<ItemDrop>();
            if (itemDrop?.m_itemData?.m_shared == null)
                return;

            if (!MegaFishingPlugin.IsFishName(itemDrop.m_itemData.m_shared.m_name))
                return;

            int max = itemDrop.m_itemData.m_shared.m_maxQuality > 0
                ? itemDrop.m_itemData.m_shared.m_maxQuality
                : 5;
            itemDrop.m_itemData.m_quality = Mathf.Min(
                itemDrop.m_itemData.m_quality + levelIncrease, max);
        }
    }
}
