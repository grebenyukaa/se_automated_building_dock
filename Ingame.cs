
#region INGAME
public static string BlockId(IMyTerminalBlock block)
{
    return $"{block.CustomName}__{block.EntityId}";
}

public static T FindTerminalBlockOnGrid<T>(IMyCubeGrid g) where T: class
{
    BoundingBoxI bb = new BoundingBoxI(g.Min, g.Max);
    foreach (var crd in BoundingBoxI.EnumeratePoints(bb))
    {
        if (g.CubeExists(crd))
        {
            IMySlimBlock sb = g.GetCubeBlock(crd);
            if (sb != null)
            {
                try {
                    T ret = (T)sb.FatBlock;
                    return ret;
                } catch (Exception) {
                }
            }
        }
    }
    return null;
}

public void Assert(string message, Func<bool> f)
{
    if (!f())
    {
        Echo($"check {message} not passed");
        throw new ArgumentException("assertion failure");
    }
}

public static float BLOCK_LENGTH = 2.5f;

public class Either<L, R>
{
    private readonly L left;
    private readonly R right;
    private readonly bool isLeft;

    public Either(L left)
    {
        this.left = left;
        this.isLeft = true;
    }

    public Either(R right)
    {
        this.right = right;
        this.isLeft = false;
    }

    public T Match<T>(Func<L, T> leftFunc, Func<R, T> rightFunc)
    => this.isLeft ? leftFunc(this.left) : rightFunc(this.right);
}

/////////////

IMyPistonBase[] frontend;
IMyPistonBase middleware;
IMyPistonBase backend;
IMyShipWelder[] welders;
List<IMyShipConnector> safetyConnectors;
List<IMyPistonBase> safetyPistons;
GridInfo gridInfo;

IEnumerator<bool> itExtend;
bool resetFinished = true;
IEnumerator<bool> itReset;
bool weldingFinished = true;
IEnumerator<bool> itWeld;

Dictionary<string, PistonState> pistonState;

public class GridInfo
{
    public GridInfo(Vector3I unitForward, Vector3I unitRight, Vector3I unitUp)
    {
        Forward = unitForward;
        Right = unitRight;
        Up = unitUp;
        Back = -Forward;
        Down = -Up;
        Left = -Right;
    }

    public Vector3I Forward { get; private set; }
    public Vector3I Back { get; private set; }
    public Vector3I Up { get; private set; }
    public Vector3I Down { get; private set; }
    public Vector3I Right { get; private set; }
    public Vector3I Left { get; private set; }
}

public static IEnumerable<Vector3I> GetWeldableNeighbourhood(GridInfo gi, IMyShipWelder w)
{
    // a big welder welds a 3x3 area in front of it + 1 block in front of it in the second front row.
    Vector3I front = w.Max + gi.Forward;
    yield return front;
    yield return front + gi.Forward;
    yield return front + gi.Right;
    yield return front + gi.Right + gi.Up;
    yield return front + gi.Right + gi.Down;
    yield return front + gi.Left;
    yield return front + gi.Left + gi.Up;
    yield return front + gi.Left + gi.Down;
    yield return front + gi.Up;
    yield return front + gi.Down;
}

public class WeldableNeighbourhood
{
    public WeldableNeighbourhood(MyGridProgram _this, IMyCubeGrid g, GridInfo gi, IMyShipWelder w)
    {
        this._this = _this;
        iterator = GetWeldingProgressIterator(GetWeldableBlocks(g, gi, w));
    }

    public IEnumerator<bool> GetWeldingProgressIterator(IEnumerable<Either<IMySlimBlock, bool>> blocks)
    {
        int leftBuildTicks = 0;
        List<IMySlimBlock> knownBlocks = new List<IMySlimBlock>();
        {
            var matched = blocks.Select(x => x.Match<Action>(
                s => () => { knownBlocks.Add(s); },
                u => () => { if (u) { leftBuildTicks += 10; } /* 200 ticks for a non-terminal block */ }
            ));
            foreach (var matchedAction in matched)
                matchedAction();
        }

        Func<bool> nbIntegrityFull = () => knownBlocks.Select(x => x.IsFullIntegrity).DefaultIfEmpty(true).All(x => x);
        Func<IEnumerable<float>> nbIntegrityRatio = () => knownBlocks.Select(x => x.BuildLevelRatio).DefaultIfEmpty(1);
        while (!nbIntegrityFull() || (leftBuildTicks != 0))
        {
            _this.Echo($"welding timer: known blcs in nb: {knownBlocks.Count}");
            _this.Echo($"welding timer: left ticks: {leftBuildTicks}");
            _this.Echo($"welding timer: nb avg integrity: {nbIntegrityRatio().Sum() / nbIntegrityRatio().Count()}");
            leftBuildTicks = Math.Max(leftBuildTicks - 1, 0);
            yield return false;
        }
        yield return true;
    }

    public static IEnumerable<Either<IMySlimBlock, bool>> GetWeldableBlocks(IMyCubeGrid g, GridInfo gi, IMyShipWelder w)
    {
        IMyCubeGrid wldGrid = w.CubeGrid;
        foreach (var wldGridCoord in GetWeldableNeighbourhood(gi, w))
        {
            var p = g.WorldToGridInteger(wldGrid.GridIntegerToWorld(wldGridCoord));

            if (g.CubeExists(p))
            {
                IMySlimBlock b = g.GetCubeBlock(p);
                if (b != null)
                    yield return new Either<IMySlimBlock, bool>(b);
                else
                    yield return new Either<IMySlimBlock, bool>(true);
            }
            else
            {
                yield return new Either<IMySlimBlock, bool>(false);
            }
        }
    }

    public IEnumerator<bool> iterator { get; private set; }
    MyGridProgram _this;
}

public class PistonState
{
    public PistonState(float curPos)
    {
        positionEps = 1e-5f;
        prevPosition = curPos;
    }

    public bool PositionChanged(float newPos)
    {
        return Math.Abs(newPos - prevPosition) >= positionEps;
    }

    public void ChangePosition(float newPos)
    {
        prevPosition = newPos;
    }

    public float prevPosition { get; private set; }
    double positionEps;
};

void AlterWelders(bool enabled)
{
    foreach (var w in welders)
    {
        w.Enabled = enabled;
    }
}

IEnumerator<bool> PistonExtendIterator(IMyPistonBase p)
{
    Assert("PistonExtendIterator: piston not null", () => p != null);
    p.Velocity = p.MaxVelocity;
    p.MaxLimit = p.HighestPosition;
    p.MinLimit = p.LowestPosition;
    pistonState[BlockId(p)] = new PistonState(p.CurrentPosition);
    yield return false;

    var st = pistonState[BlockId(p)];
    while (st.PositionChanged(p.CurrentPosition))
    {
        Echo($"extending: {p.CustomName}, status: {p.Status}, position: {p.CurrentPosition}, prev pos: {st.prevPosition}");
        st.ChangePosition(p.CurrentPosition);
        yield return false;
    }
    st.ChangePosition(p.CurrentPosition);

    p.Velocity = 0;
    yield return true;
}

IEnumerator<bool> PistonResetIterator(IMyPistonBase p)
{
    Assert("PistonResetIterator: piston not null", () => p != null);
    p.Velocity = -p.MaxVelocity;
    p.MaxLimit = p.HighestPosition;
    p.MinLimit = p.LowestPosition;
    pistonState[BlockId(p)] = new PistonState(p.CurrentPosition);
    yield return false;

    var st = pistonState[BlockId(p)];
    while (p.CurrentPosition != p.LowestPosition)
    {
        Echo($"resetting: {p.CustomName}, status: {p.Status}, cur pos: {p.CurrentPosition}, prev pos: {st.prevPosition}");
        st.ChangePosition(p.CurrentPosition);
        yield return false;
    }
    st.ChangePosition(p.CurrentPosition);

    p.Velocity = 0;
    yield return true;
}

IEnumerator<bool> GetResetIterator()
{
    AlterWelders(false);
    yield return false; 
    
    var itSafetyRetract = GetSafetyHarnessRetractIterator();
    while (itSafetyRetract.MoveNext())
    {
        Echo("safety harness retracting...");
        yield return false;
    }

    Assert("frontend pistons are not null", () => frontend.All(x => x != null));
    var itsFront = new List<IEnumerator<bool>>();
    foreach (var p in frontend)
    {
        itsFront.Add(PistonResetIterator(p));
    }

    while (itsFront.Select(it => it.MoveNext()).ToArray().Any(x => x))
    {
        yield return false;
    }

    Assert("middle piston not null", () => middleware != null);
    var itMiddle = PistonResetIterator(middleware);
    while (itMiddle.MoveNext())
    {
        yield return false;
    }

    Assert("back piston not null", () => backend != null);
    var itBackend = PistonResetIterator(backend);
    while (itBackend.MoveNext())
    {
        yield return false;
    }

    yield return true;
}

IEnumerator<bool> GetExtendIterator()
{
    AlterWelders(false);
    yield return false;

    var itBackend = PistonExtendIterator(backend);
    while (itBackend.MoveNext())
    {
        yield return false;
    }

    var itMiddle = PistonExtendIterator(middleware);
    while (itMiddle.MoveNext())
    {
        yield return false;
    }

    yield return true;
}

IEnumerator<bool> GetSafetyHarnessExtendIterator()
{
    foreach (var c in safetyConnectors)
        c.Enabled = true;
    yield return false;

    foreach(var p in safetyPistons)
        p.Velocity = BLOCK_LENGTH;
    
    while (safetyPistons.Any(p => p.CurrentPosition != p.MaxLimit))
    {
        Echo($"safety harness: extending");
        yield return false;
    }

    //while (safetyConnectors.Any(x => x.Status != MyShipConnectorStatus.Connected))
    {
        foreach (var c in safetyConnectors)
            c.Connect();
        yield return true;
    }

    yield return true;
}

IEnumerator<bool> GetSafetyHarnessRetractIterator()
{
    foreach (var c in safetyConnectors)
        c.Enabled = false;
    yield return false;

    foreach(var p in safetyPistons)
        p.Velocity = -BLOCK_LENGTH;
    
    while (safetyPistons.Any(p => p.CurrentPosition != p.MinLimit))
    {
        Echo($"safety harness: retracting");
        yield return false;
    }

    yield return true;
}

IEnumerator<bool> GetWeldingIterator()
{
    float moveDelta = BLOCK_LENGTH;

    middleware.MinLimit = middleware.CurrentPosition;
    middleware.Velocity = -middleware.MaxVelocity;
    
    backend.MinLimit = backend.CurrentPosition;
    backend.Velocity = -backend.MaxVelocity;

    while ((backend.CurrentPosition != backend.LowestPosition) || (middleware.CurrentPosition != middleware.LowestPosition))
    {
        var itSafetyExtend = GetSafetyHarnessExtendIterator();
        while (itSafetyExtend.MoveNext())
        {
            Echo("safety harness extending...");
            yield return false;
        }

        var itFrontWld = GetFrontendWeldingIterator(moveDelta);
        while (itFrontWld.MoveNext())
        {
            Echo("fontend welding cycle...");
            yield return false;
        }
        
        var itSafetyRetract = GetSafetyHarnessRetractIterator();
        while (itSafetyRetract.MoveNext())
        {
            Echo("safety harness retracting...");
            yield return false;
        }

        if (middleware.CurrentPosition != middleware.LowestPosition)
        {
            middleware.MinLimit = Math.Max(middleware.CurrentPosition - moveDelta, middleware.LowestPosition);
            Echo($"middleware: retracting, cur pos: {middleware.CurrentPosition}, min pos: {middleware.MinLimit}");
        }
        else
        {
            backend.MinLimit = Math.Max(backend.CurrentPosition - moveDelta, backend.LowestPosition);
            Echo($"backend: retracting, cur pos: {backend.CurrentPosition}, min pos: {backend.MinLimit}");
        }
        yield return false;
    }

    yield return true;
}

IEnumerator<bool> GetFrontendWeldingIterator(float moveDelta)
{
    // extending
    var itsFront = new List<IEnumerator<bool>>();
    foreach (var p in frontend)
    {
        itsFront.Add(PistonExtendIterator(p));
    }
    while (itsFront.Select(it => it.MoveNext()).ToArray().Any(x => x))
    {
        yield return false;
    }

    // welding
    foreach (var p in frontend)
    {
        p.Velocity = -p.MaxVelocity;
        p.MinLimit = p.CurrentPosition;
    }
    yield return false;

    WeldableNeighbourhood[] nbs = new WeldableNeighbourhood[welders.Length];
    for (int i = 0; i < nbs.Length; ++i)
        nbs[i] = new WeldableNeighbourhood(this, Me.CubeGrid, gridInfo, welders[i]);

    while (!frontend.All(p => p.CurrentPosition == p.LowestPosition))
    {
        Echo($"frontend: not all pistons are at lowest pos, looping...");
        
        AlterWelders(true);
        yield return false;

        // create tasks for all welders/pistons
        bool[] wsState = nbs.Select(nb => nb.iterator.MoveNext()).ToArray();
        for (int i = 0; i < welders.Length; ++i)
        {
            var p = frontend[i];
            var w = welders[i];

            if (!nbs[i].iterator.Current)
            {
                w.Enabled = true;
                Echo($"frontend: piston {p.CustomName}: welding, cur pos: {p.CurrentPosition}, min pos: {p.MinLimit}");
            }
            else
            {
                p.MinLimit = Math.Max(p.CurrentPosition - moveDelta, p.LowestPosition);
                w.Enabled = false;
                Echo($"frontend: piston {p.CustomName}: retracting, cur pos: {p.CurrentPosition}, min pos: {p.MinLimit}");
            }
        }

        // batch run pending tasks
        yield return false;

        // postprocess new positions
        for (int i = 0; i < nbs.Length; ++i)
        {
            if (nbs[i].iterator.Current)
                nbs[i] = new WeldableNeighbourhood(this, Me.CubeGrid, gridInfo, welders[i]);
        }
    }

    AlterWelders(false);
    yield return false;

    yield return true;
}

public Program()
{
    var gts = GridTerminalSystem;
    
    IMyBlockGroup frontendPst = gts.GetBlockGroupWithName("engidock piston front");
    List<IMyPistonBase> pbs = new List<IMyPistonBase>();
    frontendPst.GetBlocksOfType<IMyPistonBase>(pbs);
    frontend = pbs.ToArray();
    
    {
        IMyBlockGroup wldsGroup = gts.GetBlockGroupWithName("engidock welder");
        List<IMyShipWelder> wlds = new List<IMyShipWelder>();
        wldsGroup.GetBlocksOfType<IMyShipWelder>(wlds);

        welders = new IMyShipWelder[frontend.Length]; 
        foreach (var w in wlds)
        {
            int idx = Array.FindIndex(frontend, x => x.TopGrid == w.CubeGrid);
            welders[idx] = w;
        }
    }
    
    middleware = (IMyPistonBase)gts.GetBlockWithName("engidock piston middle");
    backend = (IMyPistonBase)gts.GetBlockWithName("engidock piston back");

    safetyConnectors = new List<IMyShipConnector>();
    IMyBlockGroup scg = gts.GetBlockGroupWithName("engidock docking connector");
    scg.GetBlocksOfType<IMyShipConnector>(safetyConnectors);
    safetyPistons = new List<IMyPistonBase>();
    IMyBlockGroup scp = gts.GetBlockGroupWithName("engidock docking piston");
    scp.GetBlocksOfType<IMyPistonBase>(safetyPistons);

    pistonState = new Dictionary<string, PistonState>();

    {
        var pivot = (IMyConveyorSorter)gts.GetBlockWithName("engidock direction marker pivot");
        var front = (IMyConveyorSorter)gts.GetBlockWithName("engidock direction marker front");
        var right = (IMyConveyorSorter)gts.GetBlockWithName("engidock direction marker right");
        var up = (IMyConveyorSorter)gts.GetBlockWithName("engidock direction marker up");
        var gridForward = front.Max - pivot.Max;
        var gridRight = right.Max - pivot.Max;
        var gridUp = up.Max - pivot.Max;
        Action<Vector3I> assertUnit = (x) => {
            Assert($"check {x} is a unit vec", () => (x == Vector3I.UnitX)
                || (x == Vector3I.UnitY)
                || (x == Vector3I.UnitZ)
            );
        };
        gridInfo = new GridInfo(gridForward, gridRight, gridUp);
    }

    Runtime.UpdateFrequency = UpdateFrequency.Update10;
}

public void Main(string argument, UpdateType updateSource)
{
    Echo($"welding finished: {weldingFinished}");
    Echo($"reset finished: {resetFinished}");

    if (updateSource != UpdateType.Update10)
    {
        bool resetRequested = argument == "reset";
        if (resetRequested)
        {
            if (resetFinished)
            {
                weldingFinished = true;
                resetFinished = false;
            }
        }
        else
        {
            if (weldingFinished && resetFinished)
            {
                weldingFinished = false;
            }
        }
    }
    else
    {
        if (!resetFinished)
        {
            if (itReset == null)
            {
                itReset = GetResetIterator();
            }
            itReset.MoveNext();
            resetFinished = itReset.Current;
            Echo($"reset routine status: {itReset.Current}");
        }
        
        if (!weldingFinished)
        {
            if (itExtend == null)
            {
                itExtend = GetExtendIterator();
            }
            itExtend.MoveNext();
            Echo($"extend routine status: {itExtend.Current}");

            if (itExtend.Current)
            {
                if (itWeld == null)
                {
                    itWeld = GetWeldingIterator();
                }
                
                itWeld.MoveNext();
                weldingFinished = itWeld.Current;
                Echo($"weld routine status: {itWeld.Current}");

                if (itWeld.Current)
                {
                    weldingFinished = true;
                    itExtend = null;
                    itWeld = null;
                }
            }
        }
    }
}
#endregion INGAME

