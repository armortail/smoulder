using UnityEngine;

namespace FireFight.Environment
{
    /// <summary>
    /// The fire spread algorithm. Pure logic: give it a grid and a config,
    /// call <see cref="Tick"/>, and it advances the simulation one step.
    ///
    /// Deliberately NOT a MonoBehaviour so the spread rules can be reasoned
    /// about and unit-tested in isolation. The MonoBehaviour layer decides
    /// WHEN to tick; this class decides WHAT happens on a tick.
    /// </summary>
    public class FireSim
    {
        // 6-directional neighbours (no diagonals): +/- X, Y, Z.
        private static readonly Vector3Int[] Neighbours =
        {
            new Vector3Int( 1, 0, 0), new Vector3Int(-1, 0, 0),
            new Vector3Int( 0, 1, 0), new Vector3Int( 0,-1, 0),
            new Vector3Int( 0, 0, 1), new Vector3Int( 0, 0,-1),
        };

        private readonly FireGrid grid;
        private readonly FireConfig config;

        // Optional confined-space oxygen pools. Empty = no oxygen simulation,
        // so fire behaves exactly as before when no boxes are supplied.
        private readonly System.Collections.Generic.List<OxygenBox> oxygenBoxes;

        // Per-cell oxygen-box lookup, indexed by grid.Index(cell). Replaces the old
        // per-cell linear scan of every box (BoxAt), which ran twice per cell per
        // tick - O(cells x boxes). The boxes are static grid regions, so this map is
        // built once and only rebuilt if the box set itself changes (open/close only
        // flips a box's internal flag, not which cells it covers). A null entry means
        // "open air" (no box). The whole array is left null when there are no boxes,
        // so the no-oxygen case allocates nothing and behaves exactly as before.
        private OxygenBox[] cellBox;
        private int cellBoxBoxCount = -1;

        // Reused each tick to avoid per-tick allocations.
        private readonly float[] heatDelta;

        // ---- Active-cell set ----
        //
        // Instead of scanning all N cells every tick, we track only the cells that
        // can actually change: burning cells, cells with residual heat/wetness/
        // coating still decaying, and the neighbours of burning cells (candidates to
        // ignite next). Idle cells - the vast majority once fire is localised - are
        // skipped entirely.
        //
        // Completeness is the whole game here: a cell MUST be in the set on the tick
        // it needs processing, or its heat/wetness/coating would freeze. We guarantee
        // that two ways:
        //   1. Every tick we re-seed the next set from this tick's still-live cells
        //      and the neighbours of every burning cell (so spread keeps reaching out).
        //   2. Any EXTERNAL change (ignite, wet, smother, starve from FireManager's
        //      API) calls MarkActive, so player/designer actions pull cells back in.
        private System.Collections.Generic.HashSet<int> active =
            new System.Collections.Generic.HashSet<int>();
        private System.Collections.Generic.HashSet<int> nextActive =
            new System.Collections.Generic.HashSet<int>();

        // Cells this tick's pass 1 wrote heat into. Pass 2 must visit these even if
        // they were not already active (they just received heat from a neighbour).
        // Tracked explicitly so we can clear only the heatDelta entries we touched,
        // instead of clearing the whole array each tick.
        private readonly System.Collections.Generic.List<int> heatedThisTick =
            new System.Collections.Generic.List<int>();

        // Scratch list of the cells pass 2 should visit (active + heated), so we can
        // iterate a stable snapshot while rebuilding nextActive. Reused each tick.
        private readonly System.Collections.Generic.List<int> toProcess =
            new System.Collections.Generic.List<int>();

        public FireSim(FireGrid grid, FireConfig config)
            : this(grid, config, null)
        {
        }

        public FireSim(FireGrid grid, FireConfig config,
            System.Collections.Generic.List<OxygenBox> oxygenBoxes)
        {
            this.grid = grid;
            this.config = config;
            this.oxygenBoxes = oxygenBoxes;
            this.heatDelta = new float[grid.Count];
        }

        /// <summary>
        /// Flag a cell (by grid position) as active so the next tick processes it.
        /// Called by FireManager whenever its API changes a cell (ignite, cool, wet,
        /// smother, starve), so externally-driven changes are never skipped by the
        /// active-set optimisation. Out-of-bounds positions are ignored.
        /// </summary>
        public void MarkActive(Vector3Int pos)
        {
            if (grid.InBounds(pos)) active.Add(grid.Index(pos));
        }

        /// <summary>
        /// Return the oxygen box containing a cell, or null (open air), using the
        /// precomputed per-cell lookup. Rebuilds the lookup if the box set changed.
        /// </summary>
        private OxygenBox BoxAt(Vector3Int pos)
        {
            if (oxygenBoxes == null || oxygenBoxes.Count == 0) return null;
            EnsureCellBoxMap();
            return cellBox[grid.Index(pos)];
        }

        /// <summary>
        /// Build (or rebuild) the cell -> oxygen-box map. Costs one pass over the
        /// boxes' cell ranges, done once up front instead of a full-box scan per
        /// cell per tick. Later boxes win on overlap, matching the old BoxAt, which
        /// returned the first match in iteration order... note: BoxAt returned the
        /// FIRST box, so we must preserve that - fill only cells not yet claimed.
        /// </summary>
        private void EnsureCellBoxMap()
        {
            if (cellBox != null && cellBoxBoxCount == oxygenBoxes.Count) return;

            if (cellBox == null) cellBox = new OxygenBox[grid.Count];
            else System.Array.Clear(cellBox, 0, cellBox.Length);

            // Iterate boxes in order; only claim a cell if an earlier box has not,
            // so the result matches the old "first box that Contains()" behaviour.
            for (int b = 0; b < oxygenBoxes.Count; b++)
            {
                OxygenBox box = oxygenBoxes[b];
                if (box == null) continue;

                int xMin = Mathf.Max(0, box.Min.x), xMax = Mathf.Min(grid.Size.x - 1, box.Max.x);
                int yMin = Mathf.Max(0, box.Min.y), yMax = Mathf.Min(grid.Size.y - 1, box.Max.y);
                int zMin = Mathf.Max(0, box.Min.z), zMax = Mathf.Min(grid.Size.z - 1, box.Max.z);

                for (int x = xMin; x <= xMax; x++)
                    for (int y = yMin; y <= yMax; y++)
                        for (int z = zMin; z <= zMax; z++)
                        {
                            int idx = grid.Index(x, y, z);
                            if (cellBox[idx] == null) cellBox[idx] = box;
                        }
            }
            cellBoxBoxCount = oxygenBoxes.Count;
        }

        /// <summary>
        /// Rebuild the active set by scanning the whole grid for cells that still
        /// need simulating: anything burning (plus its neighbours, which may ignite),
        /// and any cell with residual heat, wetness, or coating to decay. Used only
        /// when the active set is empty, to pick up cells changed directly (e.g. a
        /// test, or any code that sets cell.state without going through the API).
        /// </summary>
        private void SeedActiveFromGrid(FireCell[] cells)
        {
            for (int i = 0; i < cells.Length; i++)
            {
                FireCell cell = cells[i];
                bool live = cell.state == FireState.Burning
                    || cell.heat > 0f || cell.wetness > 0f || cell.coating > 0f;
                if (!live) continue;

                active.Add(i);

                // Burning cells' neighbours are ignition candidates next pass.
                if (cell.state == FireState.Burning)
                {
                    foreach (Vector3Int offset in Neighbours)
                    {
                        Vector3Int np = cell.gridPosition + offset;
                        if (grid.InBounds(np)) active.Add(grid.Index(np));
                    }
                }
            }
        }

        /// <summary>
        /// Advance the simulation one step.
        ///
        /// Two passes so every cell sees the SAME previous state this tick:
        ///   1. Burning cells consume fuel and accumulate heat into neighbours.
        ///   2. Apply heat, cool idle cells, and ignite cells that got hot enough.
        /// Without the split, fire would race across the whole grid in one tick.
        ///
        /// Only the ACTIVE cells (and the neighbours pass 1 heats) are visited, so
        /// cost scales with the size of the fire, not the size of the grid. The
        /// rebuilt <see cref="nextActive"/> set carries forward every cell that is
        /// still burning, still cooling/drying/decaying, or adjacent to a flame.
        /// </summary>
        public void Tick()
        {
            FireCell[] cells = grid.Cells;

            // Reconcile: if nothing is queued, scan the grid for cells that still
            // need simulating. This keeps the old contract that setting a cell's
            // state directly (cell.state = Burning) and calling Tick() just works,
            // without the caller having to call MarkActive - and it costs a full
            // scan ONLY when the active set is empty (nothing burning/decaying),
            // which is exactly when a full scan is cheap and otherwise idle anyway.
            if (active.Count == 0) SeedActiveFromGrid(cells);

            // Clear only the heatDelta entries we wrote last tick, then forget them.
            for (int k = 0; k < heatedThisTick.Count; k++) heatDelta[heatedThisTick[k]] = 0f;
            heatedThisTick.Clear();

            nextActive.Clear();

            // Pass 1: burning cells burn fuel and heat their neighbours.
            // Snapshot the active indices into toProcess so the set can be rebuilt
            // while we iterate a stable list.
            toProcess.Clear();
            foreach (int idx in active) toProcess.Add(idx);

            for (int t = 0; t < toProcess.Count; t++)
            {
                FireCell cell = cells[toProcess[t]];
                if (cell.state != FireState.Burning) continue;

                // Water smother: a burning cell that got wet enough is put out.
                // It keeps its fuel and can relight once it dries.
                if (cell.wetness >= config.wetExtinguishThreshold)
                {
                    cell.state = FireState.Unburned;
                    cell.heat = 0f;
                    continue;
                }

                // Oxygen-blocking coating (foam / CO2 / blanket / powder): a burning
                // cell sealed from air is smothered. Keeps its fuel; can relight once
                // the coating decays away.
                if (cell.coating >= config.coatingExtinguishThreshold)
                {
                    cell.state = FireState.Unburned;
                    cell.heat = 0f;
                    continue;
                }

                // Confined-space oxygen: a burning cell inside a box consumes
                // its shared air. If the box has suffocated, the flame dies down
                // instead of consuming fuel (it can relight when air returns).
                OxygenBox box = BoxAt(cell.gridPosition);
                if (box != null)
                {
                    if (!box.HasBreathableAir)
                    {
                        // Starved of oxygen: stop burning, lose heat.
                        cell.state = FireState.Unburned;
                        cell.heat = 0f;
                        continue;
                    }
                    box.ConsumeForOneBurningCell();
                }

                cell.fuel -= config.burnRate;
                if (cell.fuel <= 0f)
                {
                    cell.fuel = 0f;
                    cell.state = FireState.Burned;
                    continue;
                }

                foreach (Vector3Int offset in Neighbours)
                {
                    Vector3Int np = cell.gridPosition + offset;
                    if (!grid.InBounds(np)) continue;

                    // Convection bias: hot air rises, so the cell above a flame
                    // gets more heat and the cell below gets less. Sideways = 1x.
                    float directionMul = 1f;
                    if (offset.y > 0) directionMul = config.upwardHeatMultiplier;
                    else if (offset.y < 0) directionMul = config.downwardHeatMultiplier;

                    int ni = grid.Index(np);
                    if (heatDelta[ni] == 0f) heatedThisTick.Add(ni); // first heat this tick
                    heatDelta[ni] += config.heatPerTick * directionMul;
                    // A heated neighbour may ignite or cool in pass 2, so it must be
                    // both processed this tick and considered next tick.
                    nextActive.Add(ni);
                }
            }

            // Pass 2: apply heat, cool idle cells, ignite eligible ones. Visit every
            // currently-active cell PLUS every cell heated this tick (a freshly-heated
            // neighbour that was not already active still needs its heat applied).
            toProcess.Clear();
            foreach (int idx in active) toProcess.Add(idx);
            for (int k = 0; k < heatedThisTick.Count; k++)
                if (!active.Contains(heatedThisTick[k])) toProcess.Add(heatedThisTick[k]);

            for (int t = 0; t < toProcess.Count; t++)
            {
                int i = toProcess[t];
                FireCell cell = cells[i];

                // Water evaporates over time so a wet cell eventually dries out.
                if (cell.wetness > 0f)
                    cell.wetness = Mathf.Max(0f, cell.wetness - config.evaporationRate);

                // Smother coating decays over time (foam collapses, CO2 disperses)
                // so a sealed cell can eventually burn again unless the agent is
                // reapplied.
                if (cell.coating > 0f)
                    cell.coating = Mathf.Max(0f, cell.coating - config.coatingDecayRate);

                if (heatDelta[i] > 0f)
                {
                    // Wetness suppresses how readily a cell heats up / catches.
                    cell.heat += heatDelta[i] * Mathf.Max(0.05f, cell.EffectiveFlammability);
                }
                else if (cell.state == FireState.Unburned && cell.heat > 0f)
                {
                    cell.heat = Mathf.Max(0f, cell.heat - config.coolingRate);
                }

                // Water carries heat away: a wet cell sheds extra residual heat
                // (scaled by wetness) so a just-extinguished spot cools quickly and
                // resists flashing back from a hot neighbour.
                if (cell.wetness > 0f && cell.heat > 0f)
                    cell.heat = Mathf.Max(0f,
                        cell.heat - config.extraCoolingWhenWet * cell.wetness);

                // Cells above the configured fire ceiling never ignite, so
                // flames stay in a lower band even though smoke rises higher.
                bool belowFireCeiling = config.maxFireLayer < 0
                    || cell.gridPosition.y <= config.maxFireLayer;

                // Confined-space oxygen: a cell in a suffocated box cannot ignite.
                OxygenBox box = BoxAt(cell.gridPosition);
                bool hasAir = box == null || box.HasBreathableAir;

                // A cell sealed by a smother coating is blocked from air and
                // cannot ignite, regardless of how hot it gets.
                bool airBlockedByCoating = cell.coating >= config.coatingExtinguishThreshold;

                if (cell.CanIgnite && belowFireCeiling && hasAir && !airBlockedByCoating
                    && cell.heat >= config.ignitionThreshold)
                {
                    if (Random.value <= config.spreadChance)
                    {
                        cell.state = FireState.Burning;
                    }
                }

                // Decide whether this cell stays live for the next tick. Anything
                // still burning, still holding heat, or still wet/coated can change
                // again, so it must be re-queued. A burning cell additionally seeds
                // its neighbours below.
                if (cell.state == FireState.Burning || cell.heat > 0f
                    || cell.wetness > 0f || cell.coating > 0f)
                {
                    nextActive.Add(i);
                }

                // Keep a burning cell's neighbours active so the flame front can
                // continue to spread next tick even into currently-cold cells.
                if (cell.state == FireState.Burning)
                {
                    foreach (Vector3Int offset in Neighbours)
                    {
                        Vector3Int np = cell.gridPosition + offset;
                        if (grid.InBounds(np)) nextActive.Add(grid.Index(np));
                    }
                }
            }

            // End of tick: open boxes breathe fresh air back in. Do this last so
            // consumption and ignition this tick saw the pre-replenish oxygen.
            if (oxygenBoxes != null)
            {
                for (int i = 0; i < oxygenBoxes.Count; i++)
                    oxygenBoxes[i].Replenish();
            }

            // Swap the buffers: next tick processes what we just queued.
            var swap = active; active = nextActive; nextActive = swap;
        }
    }
}
