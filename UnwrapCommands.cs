/*
 * Copyright (C) 2025 Game4Freak.io
 * This mod is provided under the Game4Freak EULA.
 * Full legal terms can be found at https://game4freak.io/eula/
 */

using Newtonsoft.Json;
using Oxide.Core;
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;
using Random = UnityEngine.Random;

namespace Oxide.Plugins
{
    [Info("Unwrap Commands", "VisEntities", "1.0.0")]
    [Description("Run commands when items are unwrapped.")]
    public class UnwrapCommands : RustPlugin
    {
        #region Fields

        private static UnwrapCommands _plugin;
        private static Configuration _config;
        private StoredData _storedData;

        #endregion Fields

        #region Configuration

        private class Configuration
        {
            [JsonProperty("Version")]
            public string Version { get; set; }

            [JsonProperty("Log Executed Commands To Server Console")]
            public bool LogExecutedCommandsToServerConsole { get; set; }

            [JsonProperty("Require Permission To Use (unwrapcommands.use)")]
            public bool RequirePermissionToUse { get; set; }

            [JsonProperty("Unwrap Profiles")]
            public List<UnwrapProfile> UnwrapProfiles { get; set; }
        }

        private class UnwrapProfile
        {
            [JsonProperty("Enable This Profile")]
            public bool EnableThisProfile { get; set; }

            [JsonProperty("Item Shortname")]
            public string ItemShortname { get; set; }

            [JsonProperty("Match Skin ID (0 = Any Skin)")]
            public ulong MatchSkinId { get; set; }

            [JsonProperty("Match Display Name (Empty = Any Name)")]
            public string MatchDisplayName { get; set; }

            [JsonProperty("Required Permission (Empty = None)")]
            public string RequiredPermission { get; set; }

            [JsonProperty("Cooldown Between Uses (Seconds, 0 = None)")]
            public float CooldownBetweenUses { get; set; }

            [JsonProperty("Block Unwrap While On Cooldown")]
            public bool BlockUnwrapWhileOnCooldown { get; set; }

            [JsonProperty("Command Selection Mode (All, Random, Weighted)")]
            public string CommandSelectionMode { get; set; }

            [JsonProperty("Block Default Loot (Only Give Custom Rewards)")]
            public bool BlockDefaultLoot { get; set; }

            [JsonProperty("Commands To Execute")]
            public List<CommandEntry> CommandsToExecute { get; set; }

            [JsonProperty("Send Notification To Player")]
            public bool SendNotificationToPlayer { get; set; }

            [JsonProperty("Notification Message (Supports Placeholders)")]
            public string NotificationMessage { get; set; }
        }

        private class CommandEntry
        {
            [JsonProperty("Command (Supports Placeholders)")]
            public string Command { get; set; }

            [JsonProperty("Command Type (Server, Chat, Client)")]
            public string CommandType { get; set; }

            [JsonProperty("Weight (Higher = More Likely To Be Picked)")]
            public int Weight { get; set; }

            [JsonProperty("Execute Chance (0-100 Percent)")]
            public float ExecuteChance { get; set; }
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            _config = Config.ReadObject<Configuration>();

            if (string.Compare(_config.Version, Version.ToString()) < 0)
                UpdateConfig();

            SaveConfig();
        }

        protected override void LoadDefaultConfig()
        {
            _config = GetDefaultConfig();
        }

        protected override void SaveConfig()
        {
            Config.WriteObject(_config, true);
        }

        private void UpdateConfig()
        {
            PrintWarning("Config changes detected! Updating...");

            Configuration defaultConfig = GetDefaultConfig();

            if (string.Compare(_config.Version, "1.0.0") < 0)
                _config = defaultConfig;

            PrintWarning("Config update complete! Updated from version " + _config.Version + " to " + Version.ToString());
            _config.Version = Version.ToString();
        }

        private Configuration GetDefaultConfig()
        {
            return new Configuration
            {
                Version = Version.ToString(),
                LogExecutedCommandsToServerConsole = true,
                RequirePermissionToUse = false,
                UnwrapProfiles = new List<UnwrapProfile>
                {
                    new UnwrapProfile
                    {
                        EnableThisProfile = true,
                        ItemShortname = "xmas.present.small",
                        MatchSkinId = 0,
                        MatchDisplayName = "",
                        RequiredPermission = "",
                        CooldownBetweenUses = 0f,
                        BlockUnwrapWhileOnCooldown = false,
                        CommandSelectionMode = "Weighted",
                        BlockDefaultLoot = false,
                        CommandsToExecute = new List<CommandEntry>
                        {
                            new CommandEntry
                            {
                                Command = "inventory.giveto {steamid} scrap 50",
                                CommandType = "Server",
                                Weight = 70,
                                ExecuteChance = 100f
                            },
                            new CommandEntry
                            {
                                Command = "inventory.giveto {steamid} supply.signal 1",
                                CommandType = "Server",
                                Weight = 30,
                                ExecuteChance = 100f
                            }
                        },
                        SendNotificationToPlayer = true,
                        NotificationMessage = "You unwrapped a {itemname}!"
                    }
                }
            };
        }

        #endregion Configuration

        #region Stored Data

        public class StoredData
        {
            [JsonProperty("Player Cooldowns")]
            public Dictionary<ulong, Dictionary<string, double>> PlayerCooldowns { get; set; }

            public StoredData()
            {
                PlayerCooldowns = new Dictionary<ulong, Dictionary<string, double>>();
            }
        }

        private void SaveData()
        {
            if (_storedData != null)
                Interface.Oxide.DataFileSystem.WriteObject(Name, _storedData);
        }

        private void LoadData()
        {
            _storedData = Interface.Oxide.DataFileSystem.ReadObject<StoredData>(Name);
            if (_storedData == null)
                _storedData = new StoredData();
        }

        #endregion Stored Data

        #region Oxide Hooks

        private void Init()
        {
            _plugin = this;
            PermissionUtil.RegisterPermissions();
            LoadData();
        }

        private void Unload()
        {
            SaveData();
            _config = null;
            _plugin = null;
        }

        private void OnServerSave()
        {
            SaveData();
        }

        private object OnItemUnwrap(Item item, BasePlayer player, ItemModUnwrap unwrap)
        {
            if (item == null || player == null)
                return null;

            if (!player.userID.IsSteamId())
                return null;

            if (_config.RequirePermissionToUse && !PermissionUtil.HasPermission(player, PermissionUtil.USE))
                return null;

            UnwrapProfile profile = FindMatchingProfile(item);
            if (profile == null)
            {
                return null;
            }

            if (!profile.EnableThisProfile)
            {
                return null;
            }

            if (!string.IsNullOrEmpty(profile.RequiredPermission) && !PermissionUtil.HasPermission(player, profile.RequiredPermission))
            {
                return null;
            }

            string profileKey = GetProfileKey(profile);
            if (!CheckCooldown(player, profileKey, profile.CooldownBetweenUses))
            {
                float remaining = GetRemainingCooldown(player.userID, profileKey, profile.CooldownBetweenUses);

                string itemDisplayName;
                if (!string.IsNullOrEmpty(item.name))
                {
                    itemDisplayName = item.name;
                }
                else
                {
                    itemDisplayName = item.info.displayName.english;
                }

                string formattedTime = FormatTime(remaining);
                SendReplyLocalized(player, Lang.Error_Cooldown, formattedTime, itemDisplayName);

                if (profile.BlockUnwrapWhileOnCooldown)
                {
                    return true;
                }
                return null;
            }

            UpdateCooldown(player.userID, profileKey);

            if (profile.CommandsToExecute == null || profile.CommandsToExecute.Count == 0)
            {
                if (profile.BlockDefaultLoot)
                {
                    return true;
                }
                return null;
            }

            ExecuteCommands(profile.CommandSelectionMode, profile.CommandsToExecute, player, item);

            if (profile.SendNotificationToPlayer && !string.IsNullOrEmpty(profile.NotificationMessage))
            {
                string notificationMessage = PlaceholderUtil.ReplacePlaceholders(profile.NotificationMessage, player, item);
                SendReply(player, notificationMessage);
            }

            if (profile.BlockDefaultLoot)
            {
                return true;
            }
            return null;
        }

        #endregion Oxide Hooks

        #region Profile Matching

        private UnwrapProfile FindMatchingProfile(Item item)
        {
            string shortname = item.info.shortname;
            ulong skinId = item.skin;

            string displayName;
            if (item.name != null)
            {
                displayName = item.name;
            }
            else
            {
                displayName = "";
            }

            UnwrapProfile bestMatch = null;
            int bestScore = -1;

            foreach (UnwrapProfile profile in _config.UnwrapProfiles)
            {
                if (profile.ItemShortname != shortname)
                {
                    continue;
                }

                bool skinMatches = profile.MatchSkinId == 0 || profile.MatchSkinId == skinId;
                bool nameMatches = string.IsNullOrEmpty(profile.MatchDisplayName) ||
                                   string.Equals(profile.MatchDisplayName, displayName, StringComparison.OrdinalIgnoreCase);

                if (!skinMatches || !nameMatches)
                {
                    continue;
                }

                int score = 0;
                if (profile.MatchSkinId != 0)
                {
                    score += 2;
                }
                if (!string.IsNullOrEmpty(profile.MatchDisplayName))
                {
                    score += 1;
                }

                if (score > bestScore)
                {
                    bestScore = score;
                    bestMatch = profile;
                }
            }

            return bestMatch;
        }

        private string GetProfileKey(UnwrapProfile profile)
        {
            return profile.ItemShortname + "_" + profile.MatchSkinId + "_" + profile.MatchDisplayName;
        }

        #endregion Profile Matching

        #region Command Execution

        private void ExecuteCommands(string executeMode, List<CommandEntry> commands, BasePlayer player, Item item)
        {
            string mode = "";
            if (executeMode != null)
            {
                mode = executeMode.ToLower();
            }

            switch (mode)
            {
                case "all":
                    ExecuteAllCommands(commands, player, item);
                    break;
                case "random":
                    ExecuteRandomCommand(commands, player, item);
                    break;
                case "weighted":
                default:
                    ExecuteWeightedCommand(commands, player, item);
                    break;
            }
        }

        private void ExecuteAllCommands(List<CommandEntry> commands, BasePlayer player, Item item)
        {
            foreach (CommandEntry commandEntry in commands)
            {
                if (!ShouldExecuteCommand(commandEntry, player))
                {
                    continue;
                }

                ExecuteSingleCommand(commandEntry, player, item);
            }
        }

        private void ExecuteRandomCommand(List<CommandEntry> commands, BasePlayer player, Item item)
        {
            List<CommandEntry> validCommands = new List<CommandEntry>();
            foreach (CommandEntry commandEntry in commands)
            {
                if (ShouldExecuteCommand(commandEntry, player))
                {
                    validCommands.Add(commandEntry);
                }
            }

            if (validCommands.Count == 0)
            {
                return;
            }

            CommandEntry selectedCommand = validCommands[Random.Range(0, validCommands.Count)];
            ExecuteSingleCommand(selectedCommand, player, item);
        }

        private void ExecuteWeightedCommand(List<CommandEntry> commands, BasePlayer player, Item item)
        {
            List<CommandEntry> validCommands = new List<CommandEntry>();
            foreach (CommandEntry commandEntry in commands)
            {
                if (ShouldExecuteCommand(commandEntry, player))
                {
                    validCommands.Add(commandEntry);
                }
            }

            if (validCommands.Count == 0)
            {
                return;
            }

            int totalWeight = 0;
            foreach (CommandEntry commandEntry in validCommands)
            {
                totalWeight += Math.Max(1, commandEntry.Weight);
            }

            int randomValue = Random.Range(0, totalWeight);
            int cumulative = 0;

            CommandEntry selectedCommand = null;
            foreach (CommandEntry commandEntry in validCommands)
            {
                cumulative += Math.Max(1, commandEntry.Weight);
                if (randomValue < cumulative)
                {
                    selectedCommand = commandEntry;
                    break;
                }
            }

            if (selectedCommand != null)
            {
                ExecuteSingleCommand(selectedCommand, player, item);
            }
        }

        private bool ShouldExecuteCommand(CommandEntry commandEntry, BasePlayer player)
        {
            if (commandEntry.ExecuteChance < 100f)
            {
                if (Random.Range(0f, 100f) > commandEntry.ExecuteChance)
                {
                    return false;
                }
            }

            return true;
        }

        private void ExecuteSingleCommand(CommandEntry commandEntry, BasePlayer player, Item item)
        {
            string command = PlaceholderUtil.ReplacePlaceholders(commandEntry.Command, player, item);

            if (!SecurityUtil.ValidateCommand(command))
            {
                PrintWarning("Command blocked due to security validation: " + command);
                return;
            }

            string commandType = "";
            if (commandEntry.CommandType != null)
            {
                commandType = commandEntry.CommandType.ToLower();
            }

            switch (commandType)
            {
                case "chat":
                    player.Command("chat.say", "/" + command);
                    break;

                case "client":
                    player.SendConsoleCommand(command);
                    break;

                case "server":
                default:
                    Server.Command(command);
                    break;
            }

            if (_config.LogExecutedCommandsToServerConsole)
            {
                Puts("Executed: " + command + " for player " + player.displayName);
            }
        }

        #endregion Command Execution

        #region Cooldown System

        private bool CheckCooldown(BasePlayer player, string profileKey, float cooldownSeconds)
        {
            if (cooldownSeconds <= 0)
            {
                return true;
            }

            if (PermissionUtil.HasPermission(player, PermissionUtil.BYPASS_COOLDOWN))
            {
                return true;
            }

            double currentTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            Dictionary<string, double> playerCooldowns;
            if (!_storedData.PlayerCooldowns.TryGetValue(player.userID, out playerCooldowns))
            {
                return true;
            }

            double lastUse;
            if (!playerCooldowns.TryGetValue(profileKey, out lastUse))
            {
                return true;
            }

            double elapsed = currentTime - lastUse;
            return elapsed >= cooldownSeconds;
        }

        private void UpdateCooldown(ulong playerId, string profileKey)
        {
            double currentTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            Dictionary<string, double> playerCooldowns;
            if (!_storedData.PlayerCooldowns.TryGetValue(playerId, out playerCooldowns))
            {
                playerCooldowns = new Dictionary<string, double>();
                _storedData.PlayerCooldowns[playerId] = playerCooldowns;
            }

            playerCooldowns[profileKey] = currentTime;
        }

        private float GetRemainingCooldown(ulong playerId, string profileKey, float cooldownSeconds)
        {
            Dictionary<string, double> playerCooldowns;
            if (!_storedData.PlayerCooldowns.TryGetValue(playerId, out playerCooldowns))
            {
                return 0f;
            }

            double lastUse;
            if (!playerCooldowns.TryGetValue(profileKey, out lastUse))
            {
                return 0f;
            }

            double currentTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            double remaining = cooldownSeconds - (currentTime - lastUse);

            if (remaining > 0)
            {
                return (float)remaining;
            }
            return 0f;
        }

        private string FormatTime(float seconds)
        {
            int totalSeconds = (int)Math.Ceiling(seconds);

            if (totalSeconds < 60)
            {
                if (totalSeconds == 1)
                {
                    return "1 second";
                }
                return totalSeconds + " seconds";
            }

            int hours = totalSeconds / 3600;
            int minutes = (totalSeconds % 3600) / 60;
            int remainingSeconds = totalSeconds % 60;

            if (hours > 0)
            {
                string hourText;
                if (hours == 1)
                {
                    hourText = "1 hour";
                }
                else
                {
                    hourText = hours + " hours";
                }

                if (minutes > 0)
                {
                    string minuteText;
                    if (minutes == 1)
                    {
                        minuteText = "1 minute";
                    }
                    else
                    {
                        minuteText = minutes + " minutes";
                    }
                    return hourText + " " + minuteText;
                }

                return hourText;
            }

            string minText;
            if (minutes == 1)
            {
                minText = "1 minute";
            }
            else
            {
                minText = minutes + " minutes";
            }

            if (remainingSeconds > 0)
            {
                string secText;
                if (remainingSeconds == 1)
                {
                    secText = "1 second";
                }
                else
                {
                    secText = remainingSeconds + " seconds";
                }
                return minText + " " + secText;
            }

            return minText;
        }

        #endregion Cooldown System

        #region Helper Classes

        private static class PlaceholderUtil
        {
            private static readonly Regex RandomPattern = new Regex(@"\{random:(\d+):(\d+)\}", RegexOptions.Compiled);

            public static string ReplacePlaceholders(string text, BasePlayer player, Item item)
            {
                if (string.IsNullOrEmpty(text))
                {
                    return text;
                }

                string result = text;

                result = result.Replace("{playername}", SanitizeInput(player.displayName));
                result = result.Replace("{steamid}", player.UserIDString);
                result = result.Replace("{playerid}", player.UserIDString);

                Vector3 position = player.transform.position;
                result = result.Replace("{position}", position.x.ToString("F2") + " " + position.y.ToString("F2") + " " + position.z.ToString("F2"));
                result = result.Replace("{position.x}", position.x.ToString("F2"));
                result = result.Replace("{position.y}", position.y.ToString("F2"));
                result = result.Replace("{position.z}", position.z.ToString("F2"));

                result = result.Replace("{grid}", MapHelper.PositionToString(position));

                if (item != null)
                {
                    string itemDisplayName;
                    if (!string.IsNullOrEmpty(item.name))
                    {
                        itemDisplayName = item.name;
                    }
                    else
                    {
                        itemDisplayName = item.info.displayName.english;
                    }

                    result = result.Replace("{itemname}", SanitizeInput(itemDisplayName));
                    result = result.Replace("{itemshortname}", item.info.shortname);
                    result = result.Replace("{itemid}", item.info.itemid.ToString());
                    result = result.Replace("{itemamount}", item.amount.ToString());
                    result = result.Replace("{itemuid}", item.uid.Value.ToString());
                    result = result.Replace("{skinid}", item.skin.ToString());
                }

                result = result.Replace("{timestamp}", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString());
                result = result.Replace("{datetime}", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));

                result = RandomPattern.Replace(result, new MatchEvaluator(ReplaceRandomPlaceholder));

                return result;
            }

            private static string ReplaceRandomPlaceholder(Match match)
            {
                int min = int.Parse(match.Groups[1].Value);
                int max = int.Parse(match.Groups[2].Value);
                return Random.Range(min, max + 1).ToString();
            }

            private static string SanitizeInput(string input)
            {
                if (string.IsNullOrEmpty(input))
                {
                    return input;
                }

                string result = input;
                result = result.Replace(";", "");
                result = result.Replace("\"", "");
                result = result.Replace("'", "");
                result = result.Replace("`", "");
                result = result.Replace("$", "");
                result = result.Replace("&", "");
                result = result.Replace("|", "");
                result = result.Replace("\n", "");
                result = result.Replace("\r", "");
                return result;
            }
        }

        private static class SecurityUtil
        {
            private static readonly HashSet<string> BlockedCommands = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "quit", "exit", "shutdown", "server.stop", "server.restart",
                "rcon.password", "server.hostname", "server.seed",
                "oxide.unload", "oxide.reload", "o.unload", "o.reload"
            };

            private static readonly string[] BlockedPatterns =
            {
                "rcon.", "server.writecfg", "oxide.grant", "oxide.revoke",
                "o.grant", "o.revoke"
            };

            public static bool ValidateCommand(string command)
            {
                if (string.IsNullOrWhiteSpace(command))
                {
                    return false;
                }

                string lowerCommand = command.ToLower().Trim();
                string commandName = lowerCommand.Split(' ')[0];

                if (BlockedCommands.Contains(commandName))
                {
                    return false;
                }

                foreach (string pattern in BlockedPatterns)
                {
                    if (lowerCommand.StartsWith(pattern))
                    {
                        return false;
                    }
                }

                if (command.Contains(";") || command.Contains("&&") || command.Contains("||"))
                {
                    return false;
                }

                return true;
            }
        }

        #endregion Helper Classes

        #region Permissions

        private static class PermissionUtil
        {
            public const string USE = "unwrapcommands.use";
            public const string BYPASS_COOLDOWN = "unwrapcommands.bypass.cooldown";

            public static void RegisterPermissions()
            {
                _plugin.permission.RegisterPermission(USE, _plugin);
                _plugin.permission.RegisterPermission(BYPASS_COOLDOWN, _plugin);

                foreach (UnwrapProfile profile in _config.UnwrapProfiles)
                {
                    if (!string.IsNullOrEmpty(profile.RequiredPermission))
                    {
                        _plugin.permission.RegisterPermission(profile.RequiredPermission, _plugin);
                    }
                }
            }

            public static bool HasPermission(BasePlayer player, string permissionName)
            {
                return _plugin.permission.UserHasPermission(player.UserIDString, permissionName);
            }
        }

        #endregion Permissions

        #region Localization

        private class Lang
        {
            public const string Error_Cooldown = "Error.Cooldown";
        }

        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                [Lang.Error_Cooldown] = "You must wait {0} before unwrapping another {1}."
            }, this, "en");
        }

        private static string GetMessage(BasePlayer player, string messageKey, params object[] args)
        {
            string userId;
            if (player != null)
                userId = player.UserIDString;
            else
                userId = null;

            string message = _plugin.lang.GetMessage(messageKey, _plugin, userId);

            if (args.Length > 0)
                message = string.Format(message, args);

            return message;
        }

        public static void SendReplyLocalized(BasePlayer player, string messageKey, params object[] args)
        {
            string message = GetMessage(player, messageKey, args);

            if (!string.IsNullOrWhiteSpace(message))
                _plugin.SendReply(player, message);
        }

        #endregion Localization
    }
}