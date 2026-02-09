using System;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using UnityEngine;
using Oxide.Core;
using Oxide.Core.Plugins;
using Oxide.Game.Rust.Cui;
using Newtonsoft.Json;
using Rust;

namespace Oxide.Plugins
{
    [Info("PaintballArena", "Gemini", "3.0.1")]
    [Description("Advanced Paintball: Multi-Arena, Game Modes, Presets.")]
    public class PaintballArena : RustPlugin
    {
        #region Fields & Config

        private static PaintballArena _instance;
        private PluginConfig _config;
        private GameData _data;

        // State
        private enum GameState { Lobby, Starting, Active, Ending }

        // Teams
        private enum Team { None, Green, Orange, Blue, Yellow, Purple }

        // Game Modes
        private enum GameMode { TeamDeathmatch, Elimination }

        private class MatchSession
        {
            public string ArenaName;
            public string PresetKey;
            public ArenaProfile Arena;
            public MatchRules Rules;
            public GameState State = GameState.Lobby;
            public List<ulong> Players = new List<ulong>();
            public HashSet<ulong> AlivePlayers = new HashSet<ulong>();
            public Dictionary<ulong, Team> PlayerTeams = new Dictionary<ulong, Team>();
            public Team CurrentTeamA = Team.None;
            public Team CurrentTeamB = Team.None;
            public Dictionary<Team, int> TeamScores = new Dictionary<Team, int>();
            public List<BaseEntity> ArenaEntities = new List<BaseEntity>();
            public Dictionary<Team, ulong> RustTeamIDs = new Dictionary<Team, ulong>();
            public Timer GameTimer;
            public int SecondsRemaining;
        }

        private class PlayerSelection
        {
            public string ArenaName;
            public string PresetKey;
        }

        // Runtime Logic
        private MatchRules _currentRules;
        private ArenaProfile _activeArena = null;
        private readonly Dictionary<string, MatchSession> _sessions = new Dictionary<string, MatchSession>();
        private readonly Dictionary<ulong, MatchSession> _playerSessions = new Dictionary<ulong, MatchSession>();
        private readonly Dictionary<ulong, PlayerSelection> _playerSelections = new Dictionary<ulong, PlayerSelection>();
        private readonly Dictionary<ulong, PlayerRestoreData> _restoreData = new Dictionary<ulong, PlayerRestoreData>();

        // Cleanup
        private readonly List<BaseEntity> _visualSpheres = new List<BaseEntity>();

        // Timers
        private Timer _zoneTimer;

        // Toggles
        private bool _allowMeds = false;
        private bool _allowWalls = false;

        // Constants
        private const string PermAdmin = "paintballarena.admin";
        private const string LayerUI = "UI_Paintball_HUD";
        private const string LayerMenu = "UI_Paintball_Menu";
        private const string LayerJoin = "UI_Paintball_Join";
        private const string SpherePrefab = "assets/prefabs/visualization/sphere.prefab";

        #endregion

        #region Configuration

        private class PluginConfig
        {
            [JsonProperty("Min Players")] public int MinPlayers { get; set; } = 2;
            [JsonProperty("Lobby Time (s)")] public int LobbyTime { get; set; } = 10;
            [JsonProperty("Default Match Duration (s)")] public int GameDuration { get; set; } = 300;
            [JsonProperty("Zone Trigger Radius")] public float ZoneRadius { get; set; } = 1.5f;
            [JsonProperty("Debug Mode")] public bool Debug { get; set; } = false;
            [JsonProperty("Kit Settings")] public KitConfig Kit { get; set; } = new KitConfig();
        }

        private class KitConfig
        {
            public string GunShortname { get; set; } = "paintballgun";
            public string SuitShortname { get; set; } = "paintballoveralls.suit";
            public int AmmoAmount { get; set; } = 128;
            public string AmmoShortname_Default { get; set; } = "ammo.paintball";
        }

        private class MatchRules
        {
            public string PresetKey = "5v5";
            public string PresetName = "5v5 TDM";
            public GameMode Mode = GameMode.TeamDeathmatch;
            public int ScoreLimit = 10; // 0 = unlimited/elimination only
            public int TeamSize = 5;
            public bool Respawn = true;
        }

        private class ArenaProfile
        {
            public string Name;
            public List<Vector3Data> SpawnsA = new List<Vector3Data>();
            public List<Vector3Data> SpawnsB = new List<Vector3Data>();
        }

        private class GameData
        {
            public Vector3Data LobbySpawn { get; set; }
            public Vector3Data ExitSpawn { get; set; } 
            public Vector3Data SpectateSpawn { get; set; }
            
            public List<ArenaProfile> Arenas { get; set; } = new List<ArenaProfile>();
            public string ActiveArenaName { get; set; } = "Main Arena";

            public Vector3Data ZoneJoin { get; set; }
            public Vector3Data ZoneLeave { get; set; }
            public Dictionary<Team, Vector3Data> ZoneTeams { get; set; } = new Dictionary<Team, Vector3Data>();
        }

        private class Vector3Data
        {
            public float x, y, z;
            public Vector3Data(Vector3 v) { x = v.x; y = v.y; z = v.z; }
            public Vector3 ToVector3() => new Vector3(x, y, z);
        }

        private class PlayerRestoreData
        {
            public List<ItemData> Items;
            public float Health;
            public Vector3 Position;
        }

        private class ItemData
        {
            public int id;
            public int amount;
            public ulong skin;
            public string container;
            public int slot;
            public bool isBlueprint;
        }

        protected override void LoadDefaultConfig() => SaveConfig();
        protected override void LoadConfig()
        {
            base.LoadConfig();
            _config = Config.ReadObject<PluginConfig>() ?? new PluginConfig();
        }

        private void LoadData()
        {
            _data = Interface.Oxide.DataFileSystem.ReadObject<GameData>("PaintballArena_Data");
            if (_data == null) _data = new GameData();
            
            // Migration: If no arenas exist, create default from legacy data if possible, or just new
            if (_data.Arenas == null || _data.Arenas.Count == 0)
            {
                _data.Arenas = new List<ArenaProfile>();
                var main = new ArenaProfile { Name = "Main Arena" };
                // Attempt to recover legacy spawns if they existed in JSON manually, 
                // but since we redefined the class structure, we assume fresh start or manually migrated.
                // For safety, let's just ensure one arena exists.
                _data.Arenas.Add(main);
                _data.ActiveArenaName = "Main Arena";
                SaveData();
            }

            SetActiveArena(_data.ActiveArenaName);
        }

        private void SaveData() => Interface.Oxide.DataFileSystem.WriteObject("PaintballArena_Data", _data);

        private void SetActiveArena(string name)
        {
            _activeArena = _data.Arenas.FirstOrDefault(a => a.Name == name);
            if (_activeArena == null)
            {
                _activeArena = _data.Arenas[0];
                _data.ActiveArenaName = _activeArena.Name;
            }
        }

        #endregion

        #region Helpers

        private void Broadcast(string msg) => PrintToChat($"<color=#ffcc00>[PAINTBALL]</color> {msg}");
        private string Invariant(string s) => s;
        private string Invariant(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);

        private void BroadcastToSession(MatchSession session, string msg)
        {
            string text = $"<color=#ffcc00>[PAINTBALL]</color> {msg}";
            foreach (var uid in session.Players)
            {
                var p = BasePlayer.FindByID(uid);
                if (p != null) SendReply(p, text);
            }
        }

        private string GetSessionKey(string arenaName, string presetKey) => $"{arenaName}:{presetKey}";

        private MatchRules GetPresetRules(string presetKey)
        {
            switch (presetKey?.ToLower())
            {
                case "1v1":
                    return new MatchRules { PresetKey = "1v1", PresetName = "1v1 Duel", Mode = GameMode.TeamDeathmatch, ScoreLimit = 3, TeamSize = 1 };
                case "2v2":
                    return new MatchRules { PresetKey = "2v2", PresetName = "2v2 Elim", Mode = GameMode.Elimination, ScoreLimit = 0, TeamSize = 2, Respawn = false };
                default:
                    return new MatchRules { PresetKey = "5v5", PresetName = "5v5 TDM", Mode = GameMode.TeamDeathmatch, ScoreLimit = 10, TeamSize = 5 };
            }
        }

        private MatchSession GetOrCreateSession(string arenaName, string presetKey)
        {
            if (string.IsNullOrEmpty(arenaName) || string.IsNullOrEmpty(presetKey) || _data?.Arenas == null || _data.Arenas.Count == 0) return null;
            var arena = _data.Arenas.FirstOrDefault(a => a.Name == arenaName) ?? _data.Arenas.FirstOrDefault();
            if (arena == null) return null;
            var key = GetSessionKey(arena.Name, presetKey);
            if (!_sessions.TryGetValue(key, out var session))
            {
                session = new MatchSession { ArenaName = arena.Name, PresetKey = presetKey, Arena = arena, Rules = GetPresetRules(presetKey) };
                _sessions[key] = session;
            }
            else
            {
                session.Arena = arena;
                session.ArenaName = arena.Name;
                session.Rules ??= GetPresetRules(presetKey);
            }
            return session;
        }

        private MatchSession GetAdminSession() => GetOrCreateSession(_activeArena?.Name ?? _data?.ActiveArenaName, _currentRules?.PresetKey ?? "5v5");
        private bool IsValidPresetKey(string presetKey) => presetKey == "5v5" || presetKey == "1v1" || presetKey == "2v2";

        private MatchSession GetPlayerSession(ulong userId)
        {
            _playerSessions.TryGetValue(userId, out var session);
            return session;
        }

        private PlayerSelection GetPlayerSelection(ulong userId)
        {
            if (!_playerSelections.TryGetValue(userId, out var selection))
            {
                selection = new PlayerSelection();
                _playerSelections[userId] = selection;
            }
            selection.PresetKey = selection.PresetKey?.ToLower();
            if (string.IsNullOrEmpty(selection.PresetKey) || !IsValidPresetKey(selection.PresetKey)) selection.PresetKey = _currentRules?.PresetKey ?? "5v5";
            if (string.IsNullOrEmpty(selection.ArenaName) || (_data?.Arenas != null && _data.Arenas.All(a => a.Name != selection.ArenaName)))
            {
                selection.ArenaName = _activeArena?.Name ?? _data?.Arenas?.FirstOrDefault()?.Name;
            }
            return selection;
        }

        #endregion

        #region Oxide Hooks

        private void Init()
        {
            _instance = this;
            permission.RegisterPermission(PermAdmin, this);
            _currentRules = GetPresetRules("5v5");
            LoadData();
        }

        private void OnServerInitialized()
        {
            SpawnVisualSpheres();
            _zoneTimer = timer.Repeat(0.5f, 0, CheckZoneTriggers);
        }

        private void Unload()
        {
            CleanupGame();
            DestroyVisualSpheres();
            _zoneTimer?.Destroy();
            foreach(var p in BasePlayer.activePlayerList)
            {
                CuiHelper.DestroyUi(p, LayerMenu);
                CuiHelper.DestroyUi(p, LayerJoin);
            }
            _instance = null;
        }

        private void OnPlayerDisconnected(BasePlayer player)
        {
            if (GetPlayerSession(player.userID) != null) LeaveGame(player);
        }

        private void OnEntityBuilt(Planner plan, GameObject go)
        {
            var player = plan.GetOwnerPlayer();
            var session = player != null ? GetPlayerSession(player.userID) : null;
            if (session == null || session.State != GameState.Active) return;
            if (player != null)
            {
                var entity = go.GetComponent<BaseEntity>();
                if (entity != null) session.ArenaEntities.Add(entity);
            }
        }

        private object OnEntityTakeDamage(BaseCombatEntity entity, HitInfo info)
        {
            var victim = entity as BasePlayer;
            var attacker = info?.Initiator as BasePlayer;

            if (victim == null || attacker == null) return null;

            var vSession = GetPlayerSession(victim.userID);
            var aSession = GetPlayerSession(attacker.userID);
            if (vSession == null && aSession == null) return null;
            if (vSession == null || aSession == null || vSession != aSession) return true;

            var session = vSession;
            Team vTeam = GetTeam(session, victim.userID);
            Team aTeam = GetTeam(session, attacker.userID);

            bool vInGame = vTeam != Team.None;
            bool aInGame = aTeam != Team.None;

            if (vInGame != aInGame) return true; 
            if (session.State != GameState.Active) return true; 
            
            if (vTeam != session.CurrentTeamA && vTeam != session.CurrentTeamB) return true;
            if (aTeam != session.CurrentTeamA && aTeam != session.CurrentTeamB) return true;
            if (vTeam == aTeam) return true; 

            // One Shot Kill Logic
            info.damageTypes.ScaleAll(0);
            EliminatePlayer(session, attacker, victim);
            
            return true; 
        }

        private object OnItemDropped(Item item, BaseEntity entity)
        {
            if (entity is BasePlayer p && GetPlayerSession(p.userID) != null) return false;
            return null;
        }

        #endregion

        #region Public API

        [HookMethod("API_GetMatchState")]
        public Dictionary<string, object> API_GetMatchState()
        {
            try
            {
                var session = GetOrCreateSession(_activeArena?.Name ?? _data?.ActiveArenaName, _currentRules?.PresetKey ?? "5v5");
                if (session == null) return null;
                var teamCounts = new Dictionary<string, int>();
                var teamScores = new Dictionary<string, int>();

                foreach(Team t in Enum.GetValues(typeof(Team)))
                {
                    if(t == Team.None) continue;
                    teamCounts[t.ToString()] = 0;
                    teamScores[t.ToString()] = session.TeamScores.ContainsKey(t) ? session.TeamScores[t] : 0;
                }

                foreach(var p in session.Players)
                {
                    if(session.PlayerTeams.TryGetValue(p, out Team t) && t != Team.None)
                        teamCounts[t.ToString()]++;
                }

                object lobbyPos = null;
                if (_data != null && _data.LobbySpawn != null) lobbyPos = _data.LobbySpawn.ToVector3();

                return new Dictionary<string, object>
                {
                    ["State"] = session.State.ToString(),
                    ["Time"] = session.SecondsRemaining,
                    ["TeamA"] = session.CurrentTeamA.ToString(),
                    ["TeamB"] = session.CurrentTeamB.ToString(),
                    ["PlayersLobby"] = session.Players.Count,
                    ["TeamCounts"] = teamCounts,
                    ["Scores"] = teamScores,
                    ["MaxTeamSize"] = session.Rules.TeamSize,
                    ["LobbyPos"] = lobbyPos,
                    ["ScoreLimit"] = session.Rules.ScoreLimit,
                    ["Arena"] = session.Arena?.Name,
                    ["Mode"] = session.Rules.PresetName
                };
            }
            catch (Exception) { return null; }
        }

        #endregion

        #region Logic & Zones

        private void SpawnVisualSpheres()
        {
            DestroyVisualSpheres();
            void CreateSphere(Vector3Data data)
            {
                if (data == null) return;
                var ent = GameManager.server.CreateEntity(SpherePrefab, data.ToVector3());
                if (ent == null) return;
                var sphere = ent as SphereEntity;
                if (sphere != null) { sphere.currentRadius = _config.ZoneRadius; sphere.lerpRadius = _config.ZoneRadius; }
                ent.Spawn();
                _visualSpheres.Add(ent);
            }
            CreateSphere(_data.ZoneJoin);
            CreateSphere(_data.ZoneLeave);
            foreach(var kvp in _data.ZoneTeams) CreateSphere(kvp.Value);
        }

        private void DestroyVisualSpheres()
        {
            foreach(var ent in _visualSpheres) { if (ent != null && !ent.IsDestroyed) ent.Kill(); }
            _visualSpheres.Clear();
        }

        private void CheckZoneTriggers()
        {
            foreach (var player in BasePlayer.activePlayerList)
            {
                if (player == null || !player.IsConnected || player.IsDead()) continue;
                var session = GetPlayerSession(player.userID);

                // Join Game
                if (_data.ZoneJoin != null && Vector3.Distance(player.transform.position, _data.ZoneJoin.ToVector3()) < _config.ZoneRadius)
                {
                    if (session == null) TryJoinSelected(player, true);
                }
                // Leave Game
                if (_data.ZoneLeave != null && Vector3.Distance(player.transform.position, _data.ZoneLeave.ToVector3()) < _config.ZoneRadius)
                {
                    if (session != null)
                    {
                        LeaveGame(player);
                        continue;
                    }
                }

                // Team Join
                if (session != null && (session.State == GameState.Lobby || session.State == GameState.Starting))
                {
                    foreach (var kvp in _data.ZoneTeams)
                    {
                        if (Vector3.Distance(player.transform.position, kvp.Value.ToVector3()) < _config.ZoneRadius)
                        {
                            Team targetTeam = kvp.Key;
                            int currentCount = session.Players.Count(p => session.PlayerTeams.ContainsKey(p) && session.PlayerTeams[p] == targetTeam);
                            
                            // Enforce current rule set team size
                            if (GetTeam(session, player.userID) != targetTeam)
                            {
                                if (currentCount >= session.Rules.TeamSize)
                                {
                                    SendReply(player, $"Team {targetTeam} is FULL ({currentCount}/{session.Rules.TeamSize}) for Mode: {session.Rules.PresetName}!");
                                    continue;
                                }
                                session.PlayerTeams[player.userID] = targetTeam;
                                SendReply(player, $"Joined <color={GetTeamColorHex(targetTeam)}>{targetTeam}</color> ({currentCount + 1}/{session.Rules.TeamSize})");
                                UpdateHUD(player, session);
                            }
                        }
                    }
                }
            }
        }

        #endregion

        #region Game Flow

        private void StartLobbyCountdown(MatchSession session)
        {
            if (session.State == GameState.Starting) return;
            session.State = GameState.Starting;
            session.SecondsRemaining = _config.LobbyTime;
            BroadcastToSession(session, $"Match ({session.Rules.PresetName}) starting in {session.SecondsRemaining}s!");

            session.GameTimer?.Destroy();
            session.GameTimer = timer.Repeat(1f, session.SecondsRemaining, () =>
            {
                session.SecondsRemaining--;
                UpdateAllUI(session);
                if (session.SecondsRemaining <= 0) StartMatch(session, false);
            });
        }

        private void StartMatch(MatchSession session, bool force = false)
        {
            session.GameTimer?.Destroy();

            // Calculate Team Counts
            Dictionary<Team, int> counts = new Dictionary<Team, int>();
            foreach(Team t in Enum.GetValues(typeof(Team))) if(t != Team.None) counts[t] = 0;
            foreach(var p in session.Players) if (session.PlayerTeams.ContainsKey(p) && session.PlayerTeams[p] != Team.None) counts[session.PlayerTeams[p]]++;

            // Sort
            var sortedTeams = counts.Where(x => x.Value > 0).OrderByDescending(x => x.Value).Select(x => x.Key).ToList();

            if (sortedTeams.Count < 2)
            {
                if (!force)
                {
                    session.State = GameState.Lobby;
                    BroadcastToSession(session, "Need at least 2 active teams to start!");
                    return;
                }
                else
                {
                    if (sortedTeams.Count > 0) { session.CurrentTeamA = sortedTeams[0]; session.CurrentTeamB = Team.None; } // Practice
                    else { session.State = GameState.Lobby; return; }
                }
            }
            else
            {
                session.CurrentTeamA = sortedTeams[0];
                session.CurrentTeamB = sortedTeams[1];
            }

            session.State = GameState.Active;
            session.AlivePlayers.Clear();
            session.ArenaEntities.Clear();
            session.TeamScores.Clear();
            foreach(Team t in Enum.GetValues(typeof(Team))) session.TeamScores[t] = 0;

            string vsText = (session.CurrentTeamB == Team.None) ? "PRACTICE MODE" : $"{session.CurrentTeamA} VS {session.CurrentTeamB}";
            BroadcastToSession(session, $"<size=20>MATCH STARTED: {vsText}</size>");
            BroadcastToSession(session, $"MODE: {session.Rules.PresetName} on ARENA: {session.Arena.Name}");

            CreateRustTeams(session);

            foreach (var uid in session.Players)
            {
                Team t = GetTeam(session, uid);
                var p = BasePlayer.FindByID(uid);
                if (p == null) continue;
                CuiHelper.DestroyUi(p, LayerJoin);
                CuiHelper.DestroyUi(p, LayerMenu);

                if (t == session.CurrentTeamA || t == session.CurrentTeamB)
                {
                    session.AlivePlayers.Add(uid);
                    SetupPlayer(session, p, t, t == session.CurrentTeamA);
                }
                else MoveToSpectate(session, p);
            }

            session.SecondsRemaining = _config.GameDuration;
            session.GameTimer = timer.Repeat(1f, session.SecondsRemaining, () =>
            {
                session.SecondsRemaining--;
                UpdateAllUI(session);
                if (session.SecondsRemaining <= 0) EndGame(session, "Time Limit");
            });
        }

        private void EndGame(MatchSession session, string reason)
        {
            session.GameTimer?.Destroy();
            session.State = GameState.Ending;
            BroadcastToSession(session, $"<size=18>GAME OVER: {reason}</size>");

            foreach (var ent in session.ArenaEntities) { if (ent != null && !ent.IsDestroyed) ent.Kill(); }
            session.ArenaEntities.Clear();

            timer.Once(5f, () =>
            {
                foreach (var uid in session.Players.ToList())
                {
                    var p = BasePlayer.FindByID(uid);
                    if (p != null && session.Players.Contains(uid))
                    {
                        p.inventory.Strip();
                        if (_data.LobbySpawn != null) p.Teleport(_data.LobbySpawn.ToVector3());
                    }
                }
                CleanupRustTeams(session);
                session.CurrentTeamA = Team.None;
                session.CurrentTeamB = Team.None;
                session.AlivePlayers.Clear();
                session.State = GameState.Lobby;
            });
        }

        #endregion

        #region Player Actions

        private void EliminatePlayer(MatchSession session, BasePlayer attacker, BasePlayer victim)
        {
            if (!session.AlivePlayers.Contains(victim.userID)) return;

            Effect.server.Run("assets/bundled/prefabs/fx/player/flesh_hit.prefab", victim.transform.position);
            string attName = (attacker != null) ? attacker.displayName : "Arena";
            BroadcastToSession(session, $"<color=orange>{victim.displayName}</color> ELIMINATED by {attName}!");

            session.AlivePlayers.Remove(victim.userID);

            // MODE LOGIC
            if (session.Rules.Mode == GameMode.TeamDeathmatch)
            {
                if (attacker != null)
                {
                    Team aTeam = GetTeam(session, attacker.userID);
                    if (aTeam != Team.None)
                    {
                        session.TeamScores[aTeam]++;
                        if (session.Rules.ScoreLimit > 0 && session.TeamScores[aTeam] >= session.Rules.ScoreLimit)
                        {
                            UpdateAllUI(session);
                            EndGame(session, $"TEAM {aTeam} WINS!");
                            return;
                        }
                    }
                }
                UpdateAllUI(session);

                if (victim.IsConnected)
                {
                    SendReply(victim, "Respawning in 5s...");
                    timer.Once(5f, () => {
                        if (session.State == GameState.Active && session.Players.Contains(victim.userID))
                        {
                            var p = BasePlayer.FindByID(victim.userID);
                            if (p != null) {
                                Team t = GetTeam(session, victim.userID);
                                if (t != Team.None) {
                                    session.AlivePlayers.Add(victim.userID);
                                    SetupPlayer(session, p, t, t == session.CurrentTeamA);
                                }
                            }
                        }
                    });
                }
            }
            else // Elimination
            {
                UpdateAllUI(session);
                if (victim.IsConnected) MoveToSpectate(session, victim);
                CheckWinCondition(session);
            }
        }

        private void CheckWinCondition(MatchSession session)
        {
            if (session.State != GameState.Active || session.Rules.Mode == GameMode.TeamDeathmatch) return;

            int countA = session.AlivePlayers.Count(x => GetTeam(session, x) == session.CurrentTeamA);
            int countB = session.AlivePlayers.Count(x => GetTeam(session, x) == session.CurrentTeamB);

            if (session.CurrentTeamB == Team.None) { if (countA == 0) EndGame(session, "PRACTICE FINISHED"); return; }

            if (countA == 0 && countB == 0) EndGame(session, "DRAW!");
            else if (countA == 0) EndGame(session, $"TEAM {session.CurrentTeamB} WINS!");
            else if (countB == 0) EndGame(session, $"TEAM {session.CurrentTeamA} WINS!");
        }

        private void SetupPlayer(MatchSession session, BasePlayer player, Team team, bool isSideA)
        {
            AddToRustTeam(session, player, team);
            // Spawn Selection based on Active Arena
            var spawns = isSideA ? session.Arena.SpawnsA : session.Arena.SpawnsB;
            if (spawns.Count == 0) spawns = new List<Vector3Data> { _data.LobbySpawn }; // Fallback

            if (spawns.Count > 0)
            {
                var pos = spawns[UnityEngine.Random.Range(0, spawns.Count)].ToVector3();
                pos.x += UnityEngine.Random.Range(-1f, 1f);
                pos.z += UnityEngine.Random.Range(-1f, 1f);
                player.Teleport(pos);
            }

            player.inventory.Strip();
            // Give Kit
            player.inventory.GiveItem(ItemManager.CreateByName(_config.Kit.SuitShortname), player.inventory.containerWear);
            player.inventory.GiveItem(ItemManager.CreateByName(_config.Kit.GunShortname), player.inventory.containerBelt);
            player.inventory.GiveItem(ItemManager.CreateByName(_config.Kit.AmmoShortname_Default, 128), player.inventory.containerMain);
            if (_allowMeds) player.inventory.GiveItem(ItemManager.CreateByName("syringe.medical", 3), player.inventory.containerBelt);
            if (_allowWalls) player.inventory.GiveItem(ItemManager.CreateByName("barricade.sandbags", 5), player.inventory.containerBelt);

            UpdateHUD(player, session);
            string msg = $"<size=20>TEAM: <color={GetTeamColorHex(team)}>{team}</color></size>";
            player.SendConsoleCommand("chat.add", 2, 0, msg);
        }

        private void MoveToSpectate(MatchSession session, BasePlayer player)
        {
            player.inventory.Strip();
            if (_data.SpectateSpawn != null) player.Teleport(_data.SpectateSpawn.ToVector3());
            UpdateHUD(player, session);
        }

        #endregion

        #region Helpers & Teams

        private void SaveAndClearInventory(BasePlayer player)
        {
            var d = new PlayerRestoreData { Health = player.health, Position = player.transform.position, Items = new List<ItemData>() };
            foreach(var item in player.inventory.containerMain.itemList) d.Items.Add(new ItemData { id = item.info.itemid, amount = item.amount, skin = item.skin, container = "main", slot = item.position });
            foreach(var item in player.inventory.containerBelt.itemList) d.Items.Add(new ItemData { id = item.info.itemid, amount = item.amount, skin = item.skin, container = "belt", slot = item.position });
            foreach(var item in player.inventory.containerWear.itemList) d.Items.Add(new ItemData { id = item.info.itemid, amount = item.amount, skin = item.skin, container = "wear", slot = item.position });
            _restoreData[player.userID] = d;
            player.inventory.Strip();
        }

        private void RestoreInventory(BasePlayer player)
        {
            player.inventory.Strip();
            if (_restoreData.TryGetValue(player.userID, out var d))
            {
                player.Teleport(d.Position);
                player.health = d.Health;
                foreach(var i in d.Items) {
                    var item = ItemManager.CreateByItemID(i.id, i.amount, i.skin);
                    if (item != null) player.inventory.GiveItem(item);
                }
                _restoreData.Remove(player.userID);
            }
            else player.Die();
        }

        private bool TryJoinSelected(BasePlayer player, bool openMenuIfMissing)
        {
            var selection = GetPlayerSelection(player.userID);
            if (string.IsNullOrEmpty(selection.ArenaName) || string.IsNullOrEmpty(selection.PresetKey))
            {
                if (openMenuIfMissing)
                {
                    SendReply(player, "Select arena & mode to join.");
                    OpenJoinMenu(player);
                }
                return false;
            }
            var session = GetOrCreateSession(selection.ArenaName, selection.PresetKey);
            if (session == null)
            {
                SendReply(player, "Selection invalid. Please re-select arena and mode.");
                if (openMenuIfMissing) OpenJoinMenu(player);
                return false;
            }
            var currentSession = GetPlayerSession(player.userID);
            if (currentSession != null)
            {
                if (currentSession == session) { SendReply(player, "You are already in this match."); return false; }
                SendReply(player, "Leave your current match before joining another.");
                return false;
            }
            JoinGame(player, session);
            return true;
        }

        private void ShowInfo(BasePlayer player)
        {
            SendReply(player, "<color=#ffcc00>[PAINTBALL]</color> Updates:");
            SendReply(player, "• Multi-session matches per arena/preset (5v5, 1v1, 2v2) running together.");
            SendReply(player, "• Ordered join flow: select arena → mode → join, then pick team in color zone.");
            SendReply(player, "• Session-based HUD, scoring, and team caps for clearer matches.");
        }

        private void JoinGame(BasePlayer player, MatchSession session)
        {
            if (session == null || GetPlayerSession(player.userID) != null) return;
            if (_data.LobbySpawn == null) { SendReply(player, "Lobby not set."); return; }
            SaveAndClearInventory(player);
            session.Players.Add(player.userID);
            session.PlayerTeams[player.userID] = Team.None;
            _playerSessions[player.userID] = session;
            player.Teleport(_data.LobbySpawn.ToVector3());
            SendReply(player, $"Joined {session.ArenaName} ({session.Rules.PresetName}). Choose a team in the color zone.");
            CuiHelper.DestroyUi(player, LayerJoin);
            if (session.Players.Count >= _config.MinPlayers && session.State == GameState.Lobby) StartLobbyCountdown(session);
        }

        private void LeaveGame(BasePlayer player)
        {
            var session = GetPlayerSession(player.userID);
            if (session == null) return;
            session.Players.Remove(player.userID);
            session.AlivePlayers.Remove(player.userID);
            session.PlayerTeams.Remove(player.userID);
            _playerSessions.Remove(player.userID);
            player.ClearTeam();
            CuiHelper.DestroyUi(player, LayerUI);
            CuiHelper.DestroyUi(player, LayerMenu);
            CuiHelper.DestroyUi(player, LayerJoin);
            RestoreInventory(player);
            if (_data.ExitSpawn != null) player.Teleport(_data.ExitSpawn.ToVector3());
            if (session.Players.Count == 0) ResetSession(session);
        }

        private void CleanupGame()
        {
            foreach (var session in _sessions.Values.ToList())
            {
                session.GameTimer?.Destroy();
                CleanupRustTeams(session);
                foreach (var ent in session.ArenaEntities) { if (ent != null && !ent.IsDestroyed) ent.Kill(); }
                foreach (var pId in session.Players)
                {
                    var p = BasePlayer.FindByID(pId);
                    if (p != null) RestoreInventory(p);
                }
            }
            _sessions.Clear();
            _playerSessions.Clear();
        }

        private void ResetSession(MatchSession session)
        {
            session.GameTimer?.Destroy();
            CleanupRustTeams(session);
            foreach (var ent in session.ArenaEntities) { if (ent != null && !ent.IsDestroyed) ent.Kill(); }
            session.ArenaEntities.Clear();
            session.Players.Clear();
            session.AlivePlayers.Clear();
            session.PlayerTeams.Clear();
            session.CurrentTeamA = Team.None;
            session.CurrentTeamB = Team.None;
            session.TeamScores.Clear();
            session.State = GameState.Lobby;
            session.SecondsRemaining = 0;
        }

        private Team GetTeam(MatchSession session, ulong uid) => session.PlayerTeams.ContainsKey(uid) ? session.PlayerTeams[uid] : Team.None;
        private string GetTeamColorHex(Team t) {
            switch(t) {
                case Team.Green: return "#55ff55"; case Team.Orange: return "#ffaa00";
                case Team.Blue: return "#5555ff"; case Team.Yellow: return "#ffff55";
                case Team.Purple: return "#aa55ff"; default: return "#ffffff";
            }
        }

        private void CreateRustTeams(MatchSession session) {
            session.RustTeamIDs.Clear();
            foreach(Team t in Enum.GetValues(typeof(Team))) { if (t != Team.None) session.RustTeamIDs[t] = RelationshipManager.ServerInstance.CreateTeam().teamID; }
        }
        private void AddToRustTeam(MatchSession session, BasePlayer p, Team t) {
            if (!session.RustTeamIDs.ContainsKey(t)) return;
            if (p.currentTeam != 0) { RelationshipManager.ServerInstance.FindTeam(p.currentTeam)?.RemovePlayer(p.userID); p.currentTeam = 0; }
            RelationshipManager.ServerInstance.FindTeam(session.RustTeamIDs[t])?.AddPlayer(p);
        }
        private void CleanupRustTeams(MatchSession session) {
            foreach(var id in session.RustTeamIDs.Values) RelationshipManager.ServerInstance.DisbandTeam(RelationshipManager.ServerInstance.FindTeam(id));
            session.RustTeamIDs.Clear();
        }

        #endregion

        #region User Interface

        private void OpenMenu(BasePlayer player, string page = "game")
        {
            CuiHelper.DestroyUi(player, LayerMenu);
            var e = new CuiElementContainer();
            var p = e.Add(new CuiPanel { Image = { Color = "0.12 0.12 0.12 0.98" }, RectTransform = { AnchorMin = "0.01 0.15", AnchorMax = "0.18 0.9" }, CursorEnabled = true }, "Overlay", LayerMenu);

            e.Add(new CuiPanel { Image = { Color = "0.8 0.4 0 1" }, RectTransform = { AnchorMin = "0 0.92", AnchorMax = "1 1" } }, p);
            e.Add(new CuiLabel { Text = { Text = "PAINTBALL ADMIN", FontSize = 16, Align = TextAnchor.MiddleCenter, Font = "robotocondensed-bold.ttf" }, RectTransform = { AnchorMin = "0 0.92", AnchorMax = "1 1" } }, p);
            e.Add(new CuiButton { Button = { Command = "pb_ui close", Color = "0.8 0.2 0.2 1" }, RectTransform = { AnchorMin = "0.85 0.93", AnchorMax = "0.98 0.99" }, Text = { Text = "X", Align = TextAnchor.MiddleCenter, Font = "robotocondensed-bold.ttf" } }, p);

            // Tabs
            AddButtonRaw(e, p, "GAME", "pb_ui menu game", page=="game"?"0.3 0.6 0.8 1":"0.2 0.2 0.2 1", Invariant($"0.02 0.86"), Invariant($"0.32 0.91"));
            AddButtonRaw(e, p, "MODES", "pb_ui menu modes", page=="modes"?"0.3 0.6 0.8 1":"0.2 0.2 0.2 1", Invariant($"0.34 0.86"), Invariant($"0.64 0.91"));
            AddButtonRaw(e, p, "SPAWNS", "pb_ui menu setup", page=="setup"?"0.3 0.6 0.8 1":"0.2 0.2 0.2 1", Invariant($"0.66 0.86"), Invariant($"0.98 0.91"));

            float y = 0.84f, h = 0.05f, g = 0.01f;

            if (page == "game")
            {
                AddHeader(e, p, "MATCH CONTROL", y); y -= 0.04f;
                AddButtonRaw(e, p, "START MATCH", "pb_ui start", "0.2 0.6 0.2 0.9", Invariant($"0.05 {y-h}"), Invariant($"0.95 {y}")); y -= (h+g);
                AddButtonRaw(e, p, "END GAME", "pb_ui stop", "0.7 0.2 0.2 0.9", Invariant($"0.05 {y-h}"), Invariant($"0.95 {y}")); y -= (h+g);
                AddButtonRaw(e, p, "OPEN JOIN MENU", "pb_ui joinmenu", "0.2 0.4 0.7 0.9", Invariant($"0.05 {y-h}"), Invariant($"0.95 {y}")); y -= (h+g);
                
                y -= 0.02f; AddHeader(e, p, "GLOBAL", y); y -= 0.04f;
                AddButtonRaw(e, p, $"MEDS: {(_allowMeds?"ON":"OFF")}", "pb_ui toggle_meds", _allowMeds?"0.2 0.6 0.2 0.9":"0.3 0.3 0.3 0.9", Invariant($"0.05 {y-h}"), Invariant($"0.48 {y}"));
                AddButtonRaw(e, p, $"WALLS: {(_allowWalls?"ON":"OFF")}", "pb_ui toggle_walls", _allowWalls?"0.2 0.6 0.2 0.9":"0.3 0.3 0.3 0.9", Invariant($"0.52 {y-h}"), Invariant($"0.95 {y}"));
                y -= (h+g);

                y -= 0.02f; AddHeader(e, p, "COMMON LOCATIONS", y); y -= 0.04f;
                AddButtonRaw(e, p, "SET LOBBY", "pb_ui set_lobby", "0.4 0.4 0.4 0.9", Invariant($"0.05 {y-h}"), Invariant($"0.95 {y}")); y -= (h+g);
                AddButtonRaw(e, p, "REFRESH ZONES", "pb_ui refresh_zones", "0.2 0.2 0.6 0.9", Invariant($"0.05 {y-h}"), Invariant($"0.95 {y}"));
            }
            else if (page == "modes")
            {
                AddHeader(e, p, "ACTIVE ARENA: " + _activeArena.Name, y); y -= 0.04f;
                AddButtonRaw(e, p, "CREATE NEW ARENA", "pb_ui create_arena", "0.2 0.5 0.2 0.9", Invariant($"0.05 {y-h}"), Invariant($"0.95 {y}")); y -= (h+g);
                
                // Arena Selector
                AddHeader(e, p, "SELECT ARENA", y); y -= 0.04f;
                foreach(var a in _data.Arenas)
                {
                    string col = (_activeArena == a) ? "0.3 0.8 0.3 0.9" : "0.3 0.3 0.3 0.9";
                    AddButtonRaw(e, p, a.Name, $"pb_ui select_arena \"{a.Name}\"", col, Invariant($"0.05 {y-h}"), Invariant($"0.95 {y}")); y -= (h+g);
                }

                y -= 0.02f; AddHeader(e, p, $"ACTIVE RULES: {_currentRules.PresetName}", y); y -= 0.04f;
                AddButtonRaw(e, p, "SET 5v5 TDM", "pb_ui set_mode 5v5", "0.2 0.2 0.5 0.9", Invariant($"0.05 {y-h}"), Invariant($"0.95 {y}")); y -= (h+g);
                AddButtonRaw(e, p, "SET 1v1 DUEL", "pb_ui set_mode 1v1", "0.2 0.2 0.5 0.9", Invariant($"0.05 {y-h}"), Invariant($"0.95 {y}")); y -= (h+g);
                AddButtonRaw(e, p, "SET 2v2 ELIM", "pb_ui set_mode 2v2", "0.2 0.2 0.5 0.9", Invariant($"0.05 {y-h}"), Invariant($"0.95 {y}"));
            }
            else if (page == "setup")
            {
                AddHeader(e, p, "EDITING: " + _activeArena.Name, y); y -= 0.04f;
                AddButtonRaw(e, p, "ADD SPAWN SIDE A", "pb_ui set_spawn a", "0.2 0.5 0.2 0.9", Invariant($"0.05 {y-h}"), Invariant($"0.48 {y}")); y -= (h+g);
                AddButtonRaw(e, p, "ADD SPAWN SIDE B", "pb_ui set_spawn b", "0.2 0.2 0.5 0.9", Invariant($"0.05 {y-h}"), Invariant($"0.95 {y}")); y -= (h+g);
                AddButtonRaw(e, p, "CLEAR SPAWNS", "pb_ui clear_spawns", "0.6 0.2 0.2 0.9", Invariant($"0.05 {y-h}"), Invariant($"0.95 {y}"));
                
                y -= 0.04f; AddHeader(e, p, "ZONES (GLOBAL)", y); y -= 0.04f;
                AddButtonRaw(e, p, "JOIN ZONE", "pb_ui set_zone join", "0.5 0.5 0.5 0.9", Invariant($"0.05 {y-h}"), Invariant($"0.48 {y}"));
                AddButtonRaw(e, p, "LEAVE ZONE", "pb_ui set_zone leave", "0.5 0.5 0.5 0.9", Invariant($"0.52 {y-h}"), Invariant($"0.95 {y}"));
                y -= (h+g);
                string[] colors = { "GREEN", "ORANGE", "BLUE", "YELLOW", "PURPLE" };
                for(int i=0; i<5; i++) {
                    AddButtonRaw(e, p, $"SET {colors[i]}", $"pb_ui set_zone {i+1}", "0.4 0.4 0.4 0.9", Invariant($"0.05 {y-h}"), Invariant($"0.95 {y}"));
                    y -= (h+g);
                }
            }

            CuiHelper.AddUi(player, e);
        }

        private void OpenJoinMenu(BasePlayer player)
        {
            var selection = GetPlayerSelection(player.userID);
            CuiHelper.DestroyUi(player, LayerJoin);
            var e = new CuiElementContainer();
            var p = e.Add(new CuiPanel { Image = { Color = "0.12 0.12 0.12 0.98" }, RectTransform = { AnchorMin = "0.20 0.15", AnchorMax = "0.45 0.9" }, CursorEnabled = true }, "Overlay", LayerJoin);

            e.Add(new CuiPanel { Image = { Color = "0.8 0.4 0 1" }, RectTransform = { AnchorMin = "0 0.92", AnchorMax = "1 1" } }, p);
            e.Add(new CuiLabel { Text = { Text = "PAINTBALL JOIN", FontSize = 16, Align = TextAnchor.MiddleCenter, Font = "robotocondensed-bold.ttf" }, RectTransform = { AnchorMin = "0 0.92", AnchorMax = "1 1" } }, p);
            e.Add(new CuiButton { Button = { Command = "pb_ui_join close", Color = "0.8 0.2 0.2 1" }, RectTransform = { AnchorMin = "0.85 0.93", AnchorMax = "0.98 0.99" }, Text = { Text = "X", Align = TextAnchor.MiddleCenter, Font = "robotocondensed-bold.ttf" } }, p);

            float y = 0.88f, h = 0.05f, g = 0.01f;
            AddHeader(e, p, "STEP 1: SELECT ARENA", y); y -= 0.04f;
            if (_data?.Arenas != null)
            {
                foreach (var arena in _data.Arenas)
                {
                    string col = selection.ArenaName == arena.Name ? "0.3 0.8 0.3 0.9" : "0.3 0.3 0.3 0.9";
                    AddButtonRaw(e, p, arena.Name, $"pb_ui_join arena \"{arena.Name}\"", col, Invariant($"0.05 {y-h}"), Invariant($"0.95 {y}")); y -= (h+g);
                }
            }

            y -= 0.02f; AddHeader(e, p, "STEP 2: SELECT MODE", y); y -= 0.04f;
            string preset = selection.PresetKey ?? "5v5";
            AddButtonRaw(e, p, "5v5 TDM", "pb_ui_join preset 5v5", preset == "5v5" ? "0.3 0.8 0.3 0.9" : "0.3 0.3 0.3 0.9", Invariant($"0.05 {y-h}"), Invariant($"0.95 {y}")); y -= (h+g);
            AddButtonRaw(e, p, "1v1 DUEL", "pb_ui_join preset 1v1", preset == "1v1" ? "0.3 0.8 0.3 0.9" : "0.3 0.3 0.3 0.9", Invariant($"0.05 {y-h}"), Invariant($"0.95 {y}")); y -= (h+g);
            AddButtonRaw(e, p, "2v2 ELIM", "pb_ui_join preset 2v2", preset == "2v2" ? "0.3 0.8 0.3 0.9" : "0.3 0.3 0.3 0.9", Invariant($"0.05 {y-h}"), Invariant($"0.95 {y}")); y -= (h+g);

            y -= 0.02f; AddHeader(e, p, "STEP 3: JOIN", y); y -= 0.04f;
            AddButtonRaw(e, p, "JOIN MATCH", "pb_ui_join join", "0.2 0.6 0.2 0.9", Invariant($"0.05 {y-h}"), Invariant($"0.95 {y}"));

            string arenaName = selection.ArenaName ?? "Select Arena";
            string presetName = GetPresetRules(preset).PresetName;
            e.Add(new CuiLabel { Text = { Text = $"Selected: {arenaName} / {presetName}\nSelect arena & mode, then choose team in color zone.", FontSize = 11, Align = TextAnchor.MiddleCenter, Color = "0.8 0.8 0.8 1", Font = "robotocondensed-regular.ttf" }, RectTransform = { AnchorMin = "0.05 0.02", AnchorMax = "0.95 0.12" } }, p);

            CuiHelper.AddUi(player, e);
        }

        private void AddHeader(CuiElementContainer e, string p, string t, float y) {
            e.Add(new CuiLabel { Text = { Text = t, FontSize = 12, Align = TextAnchor.MiddleLeft, Color = "0.7 0.7 0.7 1", Font = "robotocondensed-regular.ttf" }, RectTransform = { AnchorMin = Invariant($"0.05 {y}"), AnchorMax = Invariant($"0.95 {y+0.03f}") } }, p);
        }
        private void AddButtonRaw(CuiElementContainer e, string p, string t, string c, string col, string min, string max) {
            e.Add(new CuiButton { Button = { Command = c, Color = col }, RectTransform = { AnchorMin = min, AnchorMax = max }, Text = { Text = t, Align = TextAnchor.MiddleCenter, FontSize = 11, Font = "robotocondensed-bold.ttf" } }, p);
        }

        private void UpdateAllUI(MatchSession session) { foreach (var uid in session.Players) { var p = BasePlayer.FindByID(uid); if (p != null) UpdateHUD(p, session); } }

        private void UpdateHUD(BasePlayer player, MatchSession session)
        {
            CuiHelper.DestroyUi(player, LayerUI);
            if (session.State != GameState.Active) return;

            var e = new CuiElementContainer();
            var p = e.Add(new CuiPanel { Image = { Color = "0.1 0.1 0.1 0.8" }, RectTransform = { AnchorMin = "0.70 0.88", AnchorMax = "0.99 0.98" }, CursorEnabled = false }, "Hud", LayerUI);

            e.Add(new CuiLabel { Text = { Text = $"<size=14>PAINTBALL ({session.Rules.PresetName})</size>   <color=#cccccc>{TimeSpan.FromSeconds(session.SecondsRemaining):mm\\:ss}</color>", FontSize = 14, Align = TextAnchor.UpperCenter, Font = "robotocondensed-bold.ttf" }, RectTransform = { AnchorMin = "0 0.6", AnchorMax = "1 0.95" } }, p);

            string sc = "";
            if (session.Rules.Mode == GameMode.TeamDeathmatch) {
                int sA = session.TeamScores.ContainsKey(session.CurrentTeamA) ? session.TeamScores[session.CurrentTeamA] : 0;
                int sB = session.TeamScores.ContainsKey(session.CurrentTeamB) ? session.TeamScores[session.CurrentTeamB] : 0;
                sc = $"<color={GetTeamColorHex(session.CurrentTeamA)}>{session.CurrentTeamA}</color> {sA} vs {sB} <color={GetTeamColorHex(session.CurrentTeamB)}>{session.CurrentTeamB}</color>";
            } else {
                int cA = session.AlivePlayers.Count(x => GetTeam(session, x) == session.CurrentTeamA);
                int cB = session.AlivePlayers.Count(x => GetTeam(session, x) == session.CurrentTeamB);
                sc = $"<color={GetTeamColorHex(session.CurrentTeamA)}>{session.CurrentTeamA}</color> {cA} vs {cB} <color={GetTeamColorHex(session.CurrentTeamB)}>{session.CurrentTeamB}</color>";
            }
            e.Add(new CuiLabel { Text = { Text = sc, FontSize = 12, Align = TextAnchor.MiddleCenter, Font = "robotocondensed-bold.ttf" }, RectTransform = { AnchorMin = "0 0.25", AnchorMax = "1 0.6" } }, p);
            
            bool isAlive = session.AlivePlayers.Contains(player.userID);
            string st = isAlive ? "<color=#aaffaa>ALIVE</color>" : "<color=#ffaaaa>DEAD</color>";
            if (!session.PlayerTeams.ContainsKey(player.userID)) st = "SPECTATING";
            e.Add(new CuiLabel { Text = { Text = st, FontSize = 10, Align = TextAnchor.LowerCenter, Font = "robotocondensed-regular.ttf" }, RectTransform = { AnchorMin = "0 0.05", AnchorMax = "1 0.25" } }, p);

            CuiHelper.AddUi(player, e);
        }

        [ConsoleCommand("pb_ui")]
        private void CmdUI(ConsoleSystem.Arg arg)
        {
            var p = arg.Connection?.player as BasePlayer;
            if (p == null || !permission.UserHasPermission(p.UserIDString, PermAdmin)) return;
            if (!arg.HasArgs(1)) return;

            string cmd = arg.GetString(0);
            switch (cmd)
            {
                case "close": CuiHelper.DestroyUi(p, LayerMenu); break;
                case "start":
                {
                    var session = GetAdminSession();
                    if (session != null) StartMatch(session, true);
                    OpenMenu(p, "game");
                    break;
                }
                case "stop":
                {
                    var session = GetAdminSession();
                    if (session != null) EndGame(session, "Admin Stopped");
                    OpenMenu(p, "game");
                    break;
                }
                case "menu": OpenMenu(p, arg.GetString(1)); break;
                case "joinmenu": OpenJoinMenu(p); break;
                case "toggle_meds": _allowMeds = !_allowMeds; OpenMenu(p, "game"); break;
                case "toggle_walls": _allowWalls = !_allowWalls; OpenMenu(p, "game"); break;
                case "set_lobby": _data.LobbySpawn = new Vector3Data(p.transform.position); SaveData(); break;
                case "set_exit": _data.ExitSpawn = new Vector3Data(p.transform.position); SaveData(); SendReply(p, "Exit Set"); break;
                case "set_spectate": _data.SpectateSpawn = new Vector3Data(p.transform.position); SaveData(); SendReply(p, "Spectate Set"); break;
                case "refresh_zones": SpawnVisualSpheres(); break;
                
                case "create_arena":
                    _data.Arenas.Add(new ArenaProfile { Name = "Arena " + (_data.Arenas.Count + 1) });
                    SaveData(); OpenMenu(p, "modes"); break;
                case "select_arena":
                    if(arg.HasArgs(2)) { SetActiveArena(arg.GetString(1)); SaveData(); OpenMenu(p, "modes"); } break;
                
                case "set_mode":
                    if(arg.HasArgs(2)) {
                        string m = arg.GetString(1).ToLower();
                        if (!IsValidPresetKey(m))
                        {
                            SendReply(p, "Unknown preset key.");
                            break;
                        }
                        _currentRules = GetPresetRules(m);
                        SendReply(p, $"Admin view set to {_currentRules.PresetName}.");
                        OpenMenu(p, "modes");
                    } break;

                case "clear_spawns": _activeArena.SpawnsA.Clear(); _activeArena.SpawnsB.Clear(); SaveData(); SendReply(p, $"Spawns cleared for {_activeArena.Name}"); break;
                case "set_spawn":
                    if(arg.HasArgs(2)) {
                        if(arg.GetString(1)=="a") { _activeArena.SpawnsA.Add(new Vector3Data(p.transform.position)); SendReply(p, $"Added Spawn A to {_activeArena.Name}"); }
                        else { _activeArena.SpawnsB.Add(new Vector3Data(p.transform.position)); SendReply(p, $"Added Spawn B to {_activeArena.Name}"); }
                        SaveData();
                    } break;

                case "set_zone":
                    if(arg.HasArgs(2)) {
                        string z = arg.GetString(1).ToLower();
                        if (z == "join") _data.ZoneJoin = new Vector3Data(p.transform.position);
                        else if (z == "leave") _data.ZoneLeave = new Vector3Data(p.transform.position);
                        else if (int.TryParse(z, out int ti) && ti >= 1 && ti <= 5) _data.ZoneTeams[(Team)ti] = new Vector3Data(p.transform.position);
                        SaveData(); SpawnVisualSpheres();
                    } break;
            }
        }

        [ConsoleCommand("pb_ui_join")]
        private void CmdUIJoin(ConsoleSystem.Arg arg)
        {
            var p = arg.Connection?.player as BasePlayer;
            if (p == null) return;
            if (!arg.HasArgs(1)) { OpenJoinMenu(p); return; }

            string cmd = arg.GetString(0);
            var selection = GetPlayerSelection(p.userID);
            switch (cmd)
            {
                case "close":
                    CuiHelper.DestroyUi(p, LayerJoin);
                    break;
                case "open":
                    OpenJoinMenu(p);
                    break;
                case "arena":
                    if (arg.HasArgs(2)) { selection.ArenaName = arg.GetString(1); OpenJoinMenu(p); }
                    break;
                case "preset":
                    if (arg.HasArgs(2)) { selection.PresetKey = arg.GetString(1).ToLower(); OpenJoinMenu(p); }
                    break;
                case "join":
                    if (!TryJoinSelected(p, false)) OpenJoinMenu(p);
                    break;
            }
        }

        [ChatCommand("pb")]
        private void CmdChat(BasePlayer player, string cmd, string[] args)
        {
            if (args.Length == 0) { if (permission.UserHasPermission(player.UserIDString, PermAdmin)) OpenMenu(player); else SendReply(player, "/pb join"); return; }
            switch (args[0].ToLower())
            {
                case "join":
                    TryJoinSelected(player, true);
                    break;
                case "leave":
                    LeaveGame(player);
                    break;
                case "menu":
                    if (permission.UserHasPermission(player.UserIDString, PermAdmin)) OpenMenu(player);
                    break;
                case "info":
                    ShowInfo(player);
                    break;
            }
        }

        #endregion
    }
}
