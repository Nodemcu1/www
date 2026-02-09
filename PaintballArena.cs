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
        private GameState _currentState = GameState.Lobby;

        // Teams
        private enum Team { None, Green, Orange, Blue, Yellow, Purple }

        // Game Modes
        private enum GameMode { TeamDeathmatch, Elimination }

        // Runtime Logic
        private MatchRules _currentRules = new MatchRules();
        private ArenaProfile _activeArena = null;

        // Participants
        private List<ulong> _players = new List<ulong>(); 
        private HashSet<ulong> _alivePlayers = new HashSet<ulong>(); 
        private Dictionary<ulong, Team> _playerTeams = new Dictionary<ulong, Team>();
        private Dictionary<ulong, PlayerRestoreData> _restoreData = new Dictionary<ulong, PlayerRestoreData>();
        
        // Match Variables
        private Team _currentTeamA = Team.None;
        private Team _currentTeamB = Team.None;
        private Dictionary<Team, int> _teamScores = new Dictionary<Team, int>();

        // Cleanup
        private List<BaseEntity> _arenaEntities = new List<BaseEntity>();
        private List<BaseEntity> _visualSpheres = new List<BaseEntity>();
        private Dictionary<Team, ulong> _rustTeamIDs = new Dictionary<Team, ulong>();

        // Timers
        private Timer _gameTimer;
        private Timer _zoneTimer;
        private int _secondsRemaining;

        // Toggles
        private bool _allowMeds = false;
        private bool _allowWalls = false;

        // Constants
        private const string PermAdmin = "paintballarena.admin";
        private const string LayerUI = "UI_Paintball_HUD";
        private const string LayerMenu = "UI_Paintball_Menu";
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
            public string PresetName = "Standard 5v5";
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

        #endregion

        #region Oxide Hooks

        private void Init()
        {
            _instance = this;
            permission.RegisterPermission(PermAdmin, this);
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
            foreach(var p in BasePlayer.activePlayerList) CuiHelper.DestroyUi(p, LayerMenu);
            _instance = null;
        }

        private void OnPlayerDisconnected(BasePlayer player)
        {
            if (_players.Contains(player.userID)) LeaveGame(player);
        }

        private void OnEntityBuilt(Planner plan, GameObject go)
        {
            if (_currentState != GameState.Active) return;
            var player = plan.GetOwnerPlayer();
            if (player != null && _players.Contains(player.userID))
            {
                var entity = go.GetComponent<BaseEntity>();
                if (entity != null) _arenaEntities.Add(entity);
            }
        }

        private object OnEntityTakeDamage(BaseCombatEntity entity, HitInfo info)
        {
            var victim = entity as BasePlayer;
            var attacker = info?.Initiator as BasePlayer;

            if (victim == null || attacker == null) return null;

            Team vTeam = GetTeam(victim.userID);
            Team aTeam = GetTeam(attacker.userID);

            bool vInGame = vTeam != Team.None;
            bool aInGame = aTeam != Team.None;

            if (!vInGame && !aInGame) return null;
            if (vInGame != aInGame) return true; 
            if (_currentState != GameState.Active) return true; 
            
            if (vTeam != _currentTeamA && vTeam != _currentTeamB) return true;
            if (aTeam != _currentTeamA && aTeam != _currentTeamB) return true;
            if (vTeam == aTeam) return true; 

            // One Shot Kill Logic
            info.damageTypes.ScaleAll(0);
            EliminatePlayer(attacker, victim);
            
            return true; 
        }

        private object OnItemDropped(Item item, BaseEntity entity)
        {
            if (entity is BasePlayer p && _players.Contains(p.userID)) return false;
            return null;
        }

        #endregion

        #region Public API

        [HookMethod("API_GetMatchState")]
        public Dictionary<string, object> API_GetMatchState()
        {
            try
            {
                var teamCounts = new Dictionary<string, int>();
                var teamScores = new Dictionary<string, int>();

                foreach(Team t in Enum.GetValues(typeof(Team)))
                {
                    if(t == Team.None) continue;
                    teamCounts[t.ToString()] = 0;
                    teamScores[t.ToString()] = _teamScores.ContainsKey(t) ? _teamScores[t] : 0;
                }

                foreach(var p in _players)
                {
                    if(_playerTeams.TryGetValue(p, out Team t) && t != Team.None)
                        teamCounts[t.ToString()]++;
                }

                object lobbyPos = null;
                if (_data != null && _data.LobbySpawn != null) lobbyPos = _data.LobbySpawn.ToVector3();

                return new Dictionary<string, object>
                {
                    ["State"] = _currentState.ToString(),
                    ["Time"] = _secondsRemaining,
                    ["TeamA"] = _currentTeamA.ToString(),
                    ["TeamB"] = _currentTeamB.ToString(),
                    ["PlayersLobby"] = _players.Count,
                    ["TeamCounts"] = teamCounts,
                    ["Scores"] = teamScores,
                    ["MaxTeamSize"] = _currentRules.TeamSize,
                    ["LobbyPos"] = lobbyPos,
                    ["ScoreLimit"] = _currentRules.ScoreLimit,
                    ["Arena"] = _activeArena.Name,
                    ["Mode"] = _currentRules.PresetName
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

                // Join Game
                if (_data.ZoneJoin != null && Vector3.Distance(player.transform.position, _data.ZoneJoin.ToVector3()) < _config.ZoneRadius)
                {
                    if (!_players.Contains(player.userID)) JoinGame(player);
                }
                // Leave Game
                if (_data.ZoneLeave != null && Vector3.Distance(player.transform.position, _data.ZoneLeave.ToVector3()) < _config.ZoneRadius)
                {
                    if (_players.Contains(player.userID)) LeaveGame(player);
                }

                // Team Join
                if (_players.Contains(player.userID) && (_currentState == GameState.Lobby || _currentState == GameState.Starting))
                {
                    foreach (var kvp in _data.ZoneTeams)
                    {
                        if (Vector3.Distance(player.transform.position, kvp.Value.ToVector3()) < _config.ZoneRadius)
                        {
                            Team targetTeam = kvp.Key;
                            int currentCount = _players.Count(p => _playerTeams.ContainsKey(p) && _playerTeams[p] == targetTeam);
                            
                            // Enforce current rule set team size
                            if (_playerTeams[player.userID] != targetTeam)
                            {
                                if (currentCount >= _currentRules.TeamSize)
                                {
                                    SendReply(player, $"Team {targetTeam} is FULL ({currentCount}/{_currentRules.TeamSize}) for Mode: {_currentRules.PresetName}!");
                                    continue;
                                }
                                _playerTeams[player.userID] = targetTeam;
                                SendReply(player, $"Joined <color={GetTeamColorHex(targetTeam)}>{targetTeam}</color> ({currentCount + 1}/{_currentRules.TeamSize})");
                                UpdateHUD(player);
                            }
                        }
                    }
                }
            }
        }

        #endregion

        #region Game Flow

        private void StartLobbyCountdown()
        {
            if (_currentState == GameState.Starting) return;
            _currentState = GameState.Starting;
            _secondsRemaining = _config.LobbyTime;
            Broadcast($"Match ({_currentRules.PresetName}) starting in {_secondsRemaining}s!");

            _gameTimer?.Destroy();
            _gameTimer = timer.Repeat(1f, _secondsRemaining, () =>
            {
                _secondsRemaining--;
                UpdateAllUI();
                if (_secondsRemaining <= 0) StartMatch(false);
            });
        }

        private void StartMatch(bool force = false)
        {
            _gameTimer?.Destroy();

            // Calculate Team Counts
            Dictionary<Team, int> counts = new Dictionary<Team, int>();
            foreach(Team t in Enum.GetValues(typeof(Team))) if(t != Team.None) counts[t] = 0;
            foreach(var p in _players) if (_playerTeams.ContainsKey(p) && _playerTeams[p] != Team.None) counts[_playerTeams[p]]++;

            // Sort
            var sortedTeams = counts.Where(x => x.Value > 0).OrderByDescending(x => x.Value).Select(x => x.Key).ToList();

            if (sortedTeams.Count < 2)
            {
                if (!force)
                {
                    _currentState = GameState.Lobby;
                    Broadcast("Need at least 2 active teams to start!");
                    return;
                }
                else
                {
                    if (sortedTeams.Count > 0) { _currentTeamA = sortedTeams[0]; _currentTeamB = Team.None; } // Practice
                    else { _currentState = GameState.Lobby; return; }
                }
            }
            else
            {
                _currentTeamA = sortedTeams[0];
                _currentTeamB = sortedTeams[1];
            }

            _currentState = GameState.Active;
            _alivePlayers.Clear();
            _arenaEntities.Clear();
            _teamScores.Clear();
            foreach(Team t in Enum.GetValues(typeof(Team))) _teamScores[t] = 0;

            string vsText = (_currentTeamB == Team.None) ? "PRACTICE MODE" : $"{_currentTeamA} VS {_currentTeamB}";
            Broadcast($"<size=20>MATCH STARTED: {vsText}</size>");
            Broadcast($"MODE: {_currentRules.PresetName} on ARENA: {_activeArena.Name}");

            CreateRustTeams();

            foreach (var uid in _players)
            {
                Team t = _playerTeams[uid];
                var p = BasePlayer.FindByID(uid);
                if (p == null) continue;
                CuiHelper.DestroyUi(p, LayerMenu);

                if (t == _currentTeamA || t == _currentTeamB)
                {
                    _alivePlayers.Add(uid);
                    SetupPlayer(p, t, t == _currentTeamA);
                }
                else MoveToSpectate(p);
            }

            _secondsRemaining = _config.GameDuration;
            _gameTimer = timer.Repeat(1f, _secondsRemaining, () =>
            {
                _secondsRemaining--;
                UpdateAllUI();
                if (_secondsRemaining <= 0) EndGame("Time Limit");
            });
        }

        private void EndGame(string reason)
        {
            _gameTimer?.Destroy();
            _currentState = GameState.Ending;
            Broadcast($"<size=18>GAME OVER: {reason}</size>");

            foreach (var ent in _arenaEntities) { if (ent != null && !ent.IsDestroyed) ent.Kill(); }
            _arenaEntities.Clear();

            timer.Once(5f, () =>
            {
                foreach (var uid in _players.ToList())
                {
                    var p = BasePlayer.FindByID(uid);
                    if (p != null && _players.Contains(uid))
                    {
                        p.inventory.Strip();
                        if (_data.LobbySpawn != null) p.Teleport(_data.LobbySpawn.ToVector3());
                    }
                }
                CleanupRustTeams();
                _currentTeamA = Team.None;
                _currentTeamB = Team.None;
                _alivePlayers.Clear();
                _currentState = GameState.Lobby;
            });
        }

        #endregion

        #region Player Actions

        private void EliminatePlayer(BasePlayer attacker, BasePlayer victim)
        {
            if (!_alivePlayers.Contains(victim.userID)) return;

            Effect.server.Run("assets/bundled/prefabs/fx/player/flesh_hit.prefab", victim.transform.position);
            string attName = (attacker != null) ? attacker.displayName : "Arena";
            Broadcast($"<color=orange>{victim.displayName}</color> ELIMINATED by {attName}!");

            _alivePlayers.Remove(victim.userID);

            // MODE LOGIC
            if (_currentRules.Mode == GameMode.TeamDeathmatch)
            {
                if (attacker != null)
                {
                    Team aTeam = GetTeam(attacker.userID);
                    if (aTeam != Team.None)
                    {
                        _teamScores[aTeam]++;
                        if (_currentRules.ScoreLimit > 0 && _teamScores[aTeam] >= _currentRules.ScoreLimit)
                        {
                            UpdateAllUI();
                            EndGame($"TEAM {aTeam} WINS!");
                            return;
                        }
                    }
                }
                UpdateAllUI();

                if (victim.IsConnected)
                {
                    SendReply(victim, "Respawning in 5s...");
                    timer.Once(5f, () => {
                        if (_currentState == GameState.Active && _players.Contains(victim.userID))
                        {
                            var p = BasePlayer.FindByID(victim.userID);
                            if (p != null) {
                                Team t = GetTeam(victim.userID);
                                if (t != Team.None) {
                                    _alivePlayers.Add(victim.userID);
                                    SetupPlayer(p, t, t == _currentTeamA);
                                }
                            }
                        }
                    });
                }
            }
            else // Elimination
            {
                UpdateAllUI();
                if (victim.IsConnected) MoveToSpectate(victim);
                CheckWinCondition();
            }
        }

        private void CheckWinCondition()
        {
            if (_currentState != GameState.Active || _currentRules.Mode == GameMode.TeamDeathmatch) return;

            int countA = _alivePlayers.Count(x => GetTeam(x) == _currentTeamA);
            int countB = _alivePlayers.Count(x => GetTeam(x) == _currentTeamB);

            if (_currentTeamB == Team.None) { if (countA == 0) EndGame("PRACTICE FINISHED"); return; }

            if (countA == 0 && countB == 0) EndGame("DRAW!");
            else if (countA == 0) EndGame($"TEAM {_currentTeamB} WINS!");
            else if (countB == 0) EndGame($"TEAM {_currentTeamA} WINS!");
        }

        private void SetupPlayer(BasePlayer player, Team team, bool isSideA)
        {
            AddToRustTeam(player, team);
            // Spawn Selection based on Active Arena
            var spawns = isSideA ? _activeArena.SpawnsA : _activeArena.SpawnsB;
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

            UpdateHUD(player);
            string msg = $"<size=20>TEAM: <color={GetTeamColorHex(team)}>{team}</color></size>";
            player.SendConsoleCommand("chat.add", 2, 0, msg);
        }

        private void MoveToSpectate(BasePlayer player)
        {
            player.inventory.Strip();
            if (_data.SpectateSpawn != null) player.Teleport(_data.SpectateSpawn.ToVector3());
            UpdateHUD(player);
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

        private void JoinGame(BasePlayer player)
        {
            if (_players.Contains(player.userID)) return;
            if (_data.LobbySpawn == null) { SendReply(player, "Lobby not set."); return; }
            SaveAndClearInventory(player);
            _players.Add(player.userID);
            _playerTeams[player.userID] = Team.None;
            player.Teleport(_data.LobbySpawn.ToVector3());
            SendReply(player, "Welcome to the Arena!");
            if (_players.Count >= _config.MinPlayers && _currentState == GameState.Lobby) StartLobbyCountdown();
        }

        private void LeaveGame(BasePlayer player)
        {
            if (!_players.Contains(player.userID)) return;
            _players.Remove(player.userID);
            _alivePlayers.Remove(player.userID);
            _playerTeams.Remove(player.userID);
            player.ClearTeam();
            CuiHelper.DestroyUi(player, LayerUI);
            CuiHelper.DestroyUi(player, LayerMenu);
            RestoreInventory(player);
            if (_data.ExitSpawn != null) player.Teleport(_data.ExitSpawn.ToVector3());
        }

        private void CleanupGame()
        {
            _gameTimer?.Destroy();
            CleanupRustTeams();
            foreach (var pId in _players)
            {
                var p = BasePlayer.FindByID(pId);
                if (p != null) RestoreInventory(p);
            }
        }

        private Team GetTeam(ulong uid) => _playerTeams.ContainsKey(uid) ? _playerTeams[uid] : Team.None;
        private string GetTeamColorHex(Team t) {
            switch(t) {
                case Team.Green: return "#55ff55"; case Team.Orange: return "#ffaa00";
                case Team.Blue: return "#5555ff"; case Team.Yellow: return "#ffff55";
                case Team.Purple: return "#aa55ff"; default: return "#ffffff";
            }
        }

        private void CreateRustTeams() {
            _rustTeamIDs.Clear();
            foreach(Team t in Enum.GetValues(typeof(Team))) { if (t != Team.None) _rustTeamIDs[t] = RelationshipManager.ServerInstance.CreateTeam().teamID; }
        }
        private void AddToRustTeam(BasePlayer p, Team t) {
            if (!_rustTeamIDs.ContainsKey(t)) return;
            if (p.currentTeam != 0) { RelationshipManager.ServerInstance.FindTeam(p.currentTeam)?.RemovePlayer(p.userID); p.currentTeam = 0; }
            RelationshipManager.ServerInstance.FindTeam(_rustTeamIDs[t])?.AddPlayer(p);
        }
        private void CleanupRustTeams() {
            foreach(var id in _rustTeamIDs.Values) RelationshipManager.ServerInstance.DisbandTeam(RelationshipManager.ServerInstance.FindTeam(id));
            _rustTeamIDs.Clear();
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

        private void AddHeader(CuiElementContainer e, string p, string t, float y) {
            e.Add(new CuiLabel { Text = { Text = t, FontSize = 12, Align = TextAnchor.MiddleLeft, Color = "0.7 0.7 0.7 1", Font = "robotocondensed-regular.ttf" }, RectTransform = { AnchorMin = Invariant($"0.05 {y}"), AnchorMax = Invariant($"0.95 {y+0.03f}") } }, p);
        }
        private void AddButtonRaw(CuiElementContainer e, string p, string t, string c, string col, string min, string max) {
            e.Add(new CuiButton { Button = { Command = c, Color = col }, RectTransform = { AnchorMin = min, AnchorMax = max }, Text = { Text = t, Align = TextAnchor.MiddleCenter, FontSize = 11, Font = "robotocondensed-bold.ttf" } }, p);
        }

        private void UpdateAllUI() { foreach (var uid in _players) { var p = BasePlayer.FindByID(uid); if (p != null) UpdateHUD(p); } }

        private void UpdateHUD(BasePlayer player)
        {
            CuiHelper.DestroyUi(player, LayerUI);
            if (_currentState != GameState.Active) return;

            var e = new CuiElementContainer();
            var p = e.Add(new CuiPanel { Image = { Color = "0.1 0.1 0.1 0.8" }, RectTransform = { AnchorMin = "0.70 0.88", AnchorMax = "0.99 0.98" }, CursorEnabled = false }, "Hud", LayerUI);

            e.Add(new CuiLabel { Text = { Text = $"<size=14>PAINTBALL ({_currentRules.PresetName})</size>   <color=#cccccc>{TimeSpan.FromSeconds(_secondsRemaining):mm\\:ss}</color>", FontSize = 14, Align = TextAnchor.UpperCenter, Font = "robotocondensed-bold.ttf" }, RectTransform = { AnchorMin = "0 0.6", AnchorMax = "1 0.95" } }, p);

            string sc = "";
            if (_currentRules.Mode == GameMode.TeamDeathmatch) {
                int sA = _teamScores.ContainsKey(_currentTeamA) ? _teamScores[_currentTeamA] : 0;
                int sB = _teamScores.ContainsKey(_currentTeamB) ? _teamScores[_currentTeamB] : 0;
                sc = $"<color={GetTeamColorHex(_currentTeamA)}>{_currentTeamA}</color> {sA} vs {sB} <color={GetTeamColorHex(_currentTeamB)}>{_currentTeamB}</color>";
            } else {
                int cA = _alivePlayers.Count(x => GetTeam(x) == _currentTeamA);
                int cB = _alivePlayers.Count(x => GetTeam(x) == _currentTeamB);
                sc = $"<color={GetTeamColorHex(_currentTeamA)}>{_currentTeamA}</color> {cA} vs {cB} <color={GetTeamColorHex(_currentTeamB)}>{_currentTeamB}</color>";
            }
            e.Add(new CuiLabel { Text = { Text = sc, FontSize = 12, Align = TextAnchor.MiddleCenter, Font = "robotocondensed-bold.ttf" }, RectTransform = { AnchorMin = "0 0.25", AnchorMax = "1 0.6" } }, p);
            
            bool isAlive = _alivePlayers.Contains(player.userID);
            string st = isAlive ? "<color=#aaffaa>ALIVE</color>" : "<color=#ffaaaa>DEAD</color>";
            if (!_playerTeams.ContainsKey(player.userID)) st = "SPECTATING";
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
                case "start": StartMatch(true); OpenMenu(p, "game"); break; 
                case "stop": EndGame("Admin Stopped"); OpenMenu(p, "game"); break;
                case "menu": OpenMenu(p, arg.GetString(1)); break;
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
                        string m = arg.GetString(1);
                        if(m=="5v5") _currentRules = new MatchRules { PresetName="5v5 TDM", Mode=GameMode.TeamDeathmatch, ScoreLimit=10, TeamSize=5 };
                        else if(m=="1v1") _currentRules = new MatchRules { PresetName="1v1 Duel", Mode=GameMode.TeamDeathmatch, ScoreLimit=3, TeamSize=1 };
                        else if(m=="2v2") _currentRules = new MatchRules { PresetName="2v2 Elim", Mode=GameMode.Elimination, ScoreLimit=0, TeamSize=2 };
                        
                        // Clear teams if mode changes to prevent overflow
                        _playerTeams.Clear(); _players.Clear();
                        Broadcast($"Game Mode switched to {_currentRules.PresetName}. Teams Reset.");
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

        [ChatCommand("pb")]
        private void CmdChat(BasePlayer player, string cmd, string[] args)
        {
            if (args.Length == 0) { if (permission.UserHasPermission(player.UserIDString, PermAdmin)) OpenMenu(player); else SendReply(player, "/pb join"); return; }
            switch (args[0].ToLower()) { case "join": JoinGame(player); break; case "leave": LeaveGame(player); break; case "menu": if (permission.UserHasPermission(player.UserIDString, PermAdmin)) OpenMenu(player); break; }
        }

        #endregion
    }
}