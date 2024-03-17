using System.Linq;
using System.Numerics;
using Content.Client.Shuttles.Systems;
using Content.Shared.Shuttles.BUIStates;
using Content.Shared.Shuttles.Components;
using Content.Shared.Shuttles.Systems;
using Content.Shared.Shuttles.UI.MapObjects;
using Robust.Client.AutoGenerated;
using Robust.Client.Graphics;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Client.UserInterface.XAML;
using Robust.Shared.Audio;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Physics.Components;
using Robust.Shared.Player;
using Robust.Shared.Random;
using Robust.Shared.Timing;
using Robust.Shared.Utility;

namespace Content.Client.Shuttles.UI;

[GenerateTypedNameReferences]
public sealed partial class MapScreen : BoxContainer
{
    [Dependency] private readonly IEntityManager _entManager = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IMapManager _mapManager = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    private readonly SharedAudioSystem _audio;
    private readonly SharedMapSystem _maps;
    private readonly ShuttleSystem _shuttles;
    private readonly SharedTransformSystem _xformSystem;

    private EntityUid? _console;
    private EntityUid? _shuttleEntity;

    private FTLState _state;
    private float _ftlDuration;

    private List<ShuttleBeaconObject> _beacons = new();
    private List<ShuttleExclusionObject> _exclusions = new();

    /// <summary>
    /// When the next FTL state change happens.
    /// </summary>
    private TimeSpan _nextFtlTime;

    private TimeSpan _nextPing;
    private TimeSpan _pingCooldown = TimeSpan.FromSeconds(3);
    private TimeSpan _nextMapDequeue;

    private float _minMapDequeue = 0.05f;
    private float _maxMapDequeue = 0.25f;

    private StyleBoxFlat _ftlStyle;

    public event Action<MapCoordinates, Angle>? RequestFTL;
    public event Action<NetEntity, Angle>? RequestBeaconFTL;

    private readonly Dictionary<MapId, BoxContainer> _mapHeadings = new();
    private readonly Dictionary<MapId, List<IMapObject>> _mapObjects = new();
    private readonly List<(MapId mapId, IMapObject mapobj)> _pendingMapObjects = new();

    /// <summary>
    /// Store the names of map object controls for re-sorting later.
    /// </summary>
    private Dictionary<Control, string> _mapObjectControls = new();

    private List<Control> _sortChildren = new();

    public MapScreen()
    {
        RobustXamlLoader.Load(this);
        IoCManager.InjectDependencies(this);

        _audio = _entManager.System<SharedAudioSystem>();
        _maps = _entManager.System<SharedMapSystem>();
        _shuttles = _entManager.System<ShuttleSystem>();
        _xformSystem = _entManager.System<SharedTransformSystem>();

        MapRebuildButton.OnPressed += MapRebuildPressed;

        OnVisibilityChanged += OnVisChange;

        MapFTLButton.OnToggled += FtlPreviewToggled;

        _ftlStyle = new StyleBoxFlat(Color.LimeGreen);
        FTLBar.ForegroundStyleBoxOverride = _ftlStyle;

        // Just pass it on up.
        MapRadar.RequestFTL += (coords, angle) =>
        {
            RequestFTL?.Invoke(coords, angle);
        };

        MapRadar.RequestBeaconFTL += (ent, angle) =>
        {
            RequestBeaconFTL?.Invoke(ent, angle);
        };

        MapBeaconsButton.OnToggled += args =>
        {
            MapRadar.ShowBeacons = args.Pressed;
        };
    }

    public void UpdateState(ShuttleMapInterfaceState state)
    {
        // Only network the accumulator due to ping making the thing fonky.
        // This should work better with predicting network states as they come in.
        _beacons = state.Destinations;
        _exclusions = state.Exclusions;
        _state = state.FTLState;
        _ftlDuration = state.FTLDuration;
        _nextFtlTime = _timing.CurTime + TimeSpan.FromSeconds(_ftlDuration);
        MapRadar.InFtl = true;
        MapFTLState.Text = Loc.GetString($"shuttle-console-ftl-state-{_state.ToString()}");

        switch (_state)
        {
            case FTLState.Available:
                SetFTLAllowed(true);
                _ftlStyle.BackgroundColor = Color.FromHex("#80C71F");
                MapRadar.InFtl = false;
                break;
            case FTLState.Starting:
                SetFTLAllowed(false);
                _ftlStyle.BackgroundColor = Color.FromHex("#169C9C");
                break;
            case FTLState.Travelling:
                SetFTLAllowed(false);
                _ftlStyle.BackgroundColor = Color.FromHex("#8932B8");
                break;
            case FTLState.Arriving:
                SetFTLAllowed(false);
                _ftlStyle.BackgroundColor = Color.FromHex("#F9801D");
                break;
            case FTLState.Cooldown:
                SetFTLAllowed(false);
                // Scroll to the FTL spot
                if (_entManager.TryGetComponent(_shuttleEntity, out TransformComponent? shuttleXform))
                {
                    var targetOffset = _maps.GetGridPosition(_shuttleEntity.Value);
                    MapRadar.SetMap(shuttleXform.MapID, targetOffset, recentering: true);
                }

                _ftlStyle.BackgroundColor = Color.FromHex("#B02E26");
                MapRadar.InFtl = false;
                break;
            // Fallback in case no FTL state or the likes.
            default:
                SetFTLAllowed(false);
                _ftlStyle.BackgroundColor = Color.FromHex("#B02E26");
                MapRadar.InFtl = false;
                break;
        }

        if (IsFTLBlocked())
        {
            MapRebuildButton.Disabled = true;
            ClearMapObjects();
        }
    }

    private void SetFTLAllowed(bool value)
    {
        if (value)
        {
            MapFTLButton.Disabled = false;
        }
        else
        {
            // Unselect FTL
            MapFTLButton.Pressed = false;
            MapRadar.FtlMode = false;
            MapFTLButton.Disabled = true;
        }
    }

    private void FtlPreviewToggled(BaseButton.ButtonToggledEventArgs obj)
    {
        MapRadar.FtlMode = obj.Pressed;
    }

    public void SetConsole(EntityUid? console)
    {
        _console = console;
    }

    public void SetShuttle(EntityUid? shuttle)
    {
        _shuttleEntity = shuttle;
        MapRadar.SetShuttle(shuttle);
    }

    private void OnVisChange(Control obj)
    {
        if (!obj.Visible)
            return;

        // Centre map screen to the shuttle.
        if (_shuttleEntity != null)
        {
            var mapPos = _xformSystem.GetMapCoordinates(_shuttleEntity.Value);
            MapRadar.SetMap(mapPos.MapId, mapPos.Position);
        }
    }

    /// <summary>
    /// Does a sonar-like effect on the map.
    /// </summary>
    public void PingMap()
    {
        if (_console != null)
        {
            _audio.PlayEntity(new SoundPathSpecifier("/Audio/Effects/Shuttle/radar_ping.ogg"), Filter.Local(), _console.Value, true);
        }

        RebuildMapObjects();
        BumpMapDequeue();

        _nextPing = _timing.CurTime + _pingCooldown;
        MapRebuildButton.Disabled = true;
    }

    private void BumpMapDequeue()
    {
        _nextMapDequeue = _timing.CurTime + TimeSpan.FromSeconds(_random.NextFloat(_minMapDequeue, _maxMapDequeue));
    }

    private void MapRebuildPressed(BaseButton.ButtonEventArgs obj)
    {
        PingMap();
    }

    /// <summary>
    /// Clears all sector objects across all maps (e.g. if we start FTLing or need to re-ping).
    /// </summary>
    private void ClearMapObjects()
    {
        _mapObjectControls.Clear();
        HyperspaceDestinations.DisposeAllChildren();
        _pendingMapObjects.Clear();
        _mapObjects.Clear();
        _mapHeadings.Clear();
    }

    /// <summary>
    /// Gets all map objects at time of ping and adds them to pending to be added over time.
    /// </summary>
    private void RebuildMapObjects()
    {
        ClearMapObjects();

        if (_shuttleEntity == null)
            return;

        var mapComps = _entManager.AllEntityQueryEnumerator<MapComponent, TransformComponent, MetaDataComponent>();
        MapId ourMap = MapId.Nullspace;

        if (_entManager.TryGetComponent(_shuttleEntity, out TransformComponent? shuttleXform))
        {
            ourMap = shuttleXform.MapID;
        }

        while (mapComps.MoveNext(out var mapComp, out var mapXform, out var mapMetadata))
        {
            if (!_shuttles.CanFTLTo(_shuttleEntity.Value, mapComp.MapId))
               continue;

            var mapName = mapMetadata.EntityName;

            if (string.IsNullOrEmpty(mapName))
            {
                mapName = Loc.GetString("shuttle-console-unknown");
            }

            var heading = new CollapsibleHeading(mapName);

            heading.MinHeight = 32f;
            heading.AddStyleClass(ContainerButton.StyleClassButton);
            heading.HorizontalAlignment = HAlignment.Stretch;
            heading.Label.HorizontalAlignment = HAlignment.Center;
            heading.Label.HorizontalExpand = true;
            heading.HorizontalExpand = true;

            var gridContents = new BoxContainer()
            {
                Orientation = LayoutOrientation.Vertical,
                VerticalExpand = true,
            };

            var body = new CollapsibleBody()
            {
                HorizontalAlignment = HAlignment.Stretch,
                VerticalAlignment = VAlignment.Top,
                HorizontalExpand = true,
                Children =
                {
                    gridContents
                }
            };

            var mapButton = new Collapsible(heading, body);

            heading.OnToggled += args =>
            {
                if (args.Pressed)
                {
                    HideOtherCollapsibles(mapButton);
                }
            };

            _mapHeadings.Add(mapComp.MapId, gridContents);

            foreach (var grid in _mapManager.GetAllMapGrids(mapComp.MapId))
            {
                _entManager.TryGetComponent(grid.Owner, out IFFComponent? iffComp);

                var gridObj = new GridMapObject()
                {
                    Name = _entManager.GetComponent<MetaDataComponent>(grid.Owner).EntityName,
                    Entity = grid.Owner,
                    HideButton = iffComp != null && (iffComp.Flags & IFFFlags.HideLabel) != 0x0,
                };

                // Always show our shuttle immediately
                if (grid.Owner == _shuttleEntity)
                {
                    AddMapObject(mapComp.MapId, gridObj);
                }
                else if (iffComp == null ||
                         (iffComp.Flags & IFFFlags.Hide) == 0x0)
                {
                    _pendingMapObjects.Add((mapComp.MapId, gridObj));
                }
            }

            foreach (var (beacon, _) in _shuttles.GetExclusions(mapComp.MapId, _exclusions))
            {
                _pendingMapObjects.Add((mapComp.MapId, beacon));
            }

            foreach (var (beacon, _) in _shuttles.GetBeacons(mapComp.MapId, _beacons))
            {
                _pendingMapObjects.Add((mapComp.MapId, beacon));
            }

            HyperspaceDestinations.AddChild(mapButton);

            // Zoom in to our map
            if (mapComp.MapId == MapRadar.ViewingMap)
            {
                mapButton.BodyVisible = true;
            }
        }

        // Need to sort from furthest way to nearest (as we will pop from the end of the list first).
        // Also prioritise those on our map first.
        var shuttlePos = _xformSystem.GetWorldPosition(_shuttleEntity.Value);

        _pendingMapObjects.Sort((x, y) =>
        {
            if (x.mapId == ourMap && y.mapId != ourMap)
                return 1;

            if (y.mapId == ourMap && x.mapId != ourMap)
                return -1;

            var yMapPos = _shuttles.GetMapCoordinates(y.mapobj);
            var xMapPos = _shuttles.GetMapCoordinates(x.mapobj);

            return (yMapPos.Position - shuttlePos).Length().CompareTo((xMapPos.Position - shuttlePos).Length());
        });
    }

    /// <summary>
    /// Hides other maps upon the specified collapsible being selected (AKA hacky collapsible groups).
    /// </summary>
    private void HideOtherCollapsibles(Collapsible collapsible)
    {
        foreach (var child in HyperspaceDestinations.Children)
        {
            if (child is not Collapsible childCollapse || childCollapse == collapsible)
                continue;

            childCollapse.BodyVisible = false;
        }
    }

    /// <summary>
    /// Returns true if we shouldn't be able to select the FTL button.
    /// </summary>
    private bool IsFTLBlocked()
    {
        switch (_state)
        {
            case FTLState.Available:
                return false;
            default:
                return true;
        }
    }

    private void OnMapObjectPress(IMapObject mapObject)
    {
        if (IsFTLBlocked())
            return;

        var coordinates = _shuttles.GetMapCoordinates(mapObject);

        // If it's our map then scroll, otherwise just set position there.
        MapRadar.SetMap(coordinates.MapId, coordinates.Position, recentering: true);
    }

    public void SetMap(MapId mapId, Vector2 position)
    {
        MapRadar.SetMap(mapId, position);
        MapRadar.Offset = position;
    }

    /// <summary>
    /// Adds a map object to the specified sector map.
    /// </summary>
    private void AddMapObject(MapId mapId, IMapObject mapObj)
    {
        var existing = _mapObjects.GetOrNew(mapId);
        existing.Add(mapObj);

        if (mapObj.HideButton)
            return;

        var gridContents = _mapHeadings[mapId];

        var gridButton = new Button()
        {
            Text = mapObj.Name,
            HorizontalExpand = true,
        };

        var gridContainer = new BoxContainer()
        {
            Children =
            {
                new Control()
                {
                    MinWidth = 32f,
                },
                gridButton
            }
        };

        _mapObjectControls.Add(gridContainer, mapObj.Name);
        gridContents.AddChild(gridContainer);

        gridButton.OnPressed += args =>
        {
            OnMapObjectPress(mapObj);
        };

        if (gridContents.ChildCount > 1)
        {
            // Re-sort the children
            _sortChildren.Clear();

            foreach (var child in gridContents.Children)
            {
                DebugTools.Assert(_mapObjectControls.ContainsKey(child));
                _sortChildren.Add(child);
            }

            foreach (var child in _sortChildren)
            {
                child.Orphan();
            }

            _sortChildren.Sort((x, y) =>
            {
                var xText = _mapObjectControls[x];
                var yText = _mapObjectControls[y];

                return string.Compare(xText, yText, StringComparison.CurrentCultureIgnoreCase);
            });

            foreach (var control in _sortChildren)
            {
                gridContents.AddChild(control);
            }
        }
    }

    protected override void FrameUpdate(FrameEventArgs args)
    {
        base.FrameUpdate(args);

        var curTime = _timing.CurTime;

        if (_nextMapDequeue < curTime && _pendingMapObjects.Count > 0)
        {
            var mapObj = _pendingMapObjects[^1];
            _pendingMapObjects.RemoveAt(_pendingMapObjects.Count - 1);
            AddMapObject(mapObj.mapId, mapObj.mapobj);
            BumpMapDequeue();
        }

        if (!IsFTLBlocked() && _nextPing < curTime)
        {
            MapRebuildButton.Disabled = false;
        }

        var ftlDiff = (float) (_nextFtlTime - _timing.CurTime).TotalSeconds;

        float ftlRatio;

        if (_ftlDuration.Equals(0f))
        {
            ftlRatio = 1f;
        }
        else
        {
            ftlRatio = Math.Clamp(1f - (ftlDiff / _ftlDuration), 0f, 1f);
        }

        FTLBar.Value = ftlRatio;
    }

    protected override void Draw(DrawingHandleScreen handle)
    {
        MapRadar.SetMapObjects(_mapObjects);
        base.Draw(handle);
    }

    public void Startup()
    {
        if (_entManager.TryGetComponent(_shuttleEntity, out TransformComponent? shuttleXform))
        {
            SetMap(shuttleXform.MapID, _maps.GetGridPosition((_shuttleEntity.Value, null, shuttleXform)));
        }
    }
}
