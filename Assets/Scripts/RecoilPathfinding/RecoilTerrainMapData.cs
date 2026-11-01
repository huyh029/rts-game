using System;
using UnityEngine;

namespace RecoilPathfinding
{
    public enum RecoilCastleTeam : byte
    {
        Blue,
        Red
    }

    [Serializable]
    public struct RecoilCastleBlockData
    {
        [SerializeField] private RecoilCastleTeam team;
        [SerializeField] private Bounds worldBounds;

        public RecoilCastleTeam Team => team;
        public Bounds WorldBounds => worldBounds;

        public RecoilCastleBlockData(RecoilCastleTeam team, Bounds worldBounds)
        {
            this.team = team;
            this.worldBounds = worldBounds;
        }
    }

    // Immutable baked height and occupancy data for terrain or mesh maps. No NavMesh dependency.
    public sealed class RecoilTerrainMapData : ScriptableObject
    {
        [SerializeField] private int formatVersion = 1;
        [SerializeField] private Vector3 worldOrigin;
        [SerializeField] private Vector2 worldSize;
        [SerializeField] private int columns;
        [SerializeField] private int rows;
        [SerializeField] private float maxSlopeDegrees;
        [SerializeField, HideInInspector] private float[] heights = Array.Empty<float>();
        [SerializeField, HideInInspector] private byte[] walkableCells = Array.Empty<byte>();
        [SerializeField] private RecoilCastleBlockData[] castleBlocks = Array.Empty<RecoilCastleBlockData>();

        public int FormatVersion => formatVersion;
        public Vector3 WorldOrigin => worldOrigin;
        public Vector2 WorldSize => worldSize;
        public int Columns => columns;
        public int Rows => rows;
        public Vector2 CellSize => new Vector2(worldSize.x / (columns - 1), worldSize.y / (rows - 1));
        public float MaxSlopeDegrees => maxSlopeDegrees;
        public int CastleBlockCount => castleBlocks == null ? 0 : castleBlocks.Length;
        public bool HasValidData => formatVersion == 1 && columns >= 2 && rows >= 2 &&
            worldSize.x > 0f && worldSize.y > 0f && heights != null && walkableCells != null &&
            heights.LongLength == (long)columns * rows &&
            walkableCells.LongLength == (long)(columns - 1) * (rows - 1);

        public float HeightAtVertex(int x, int z) => worldOrigin.y + heights[z * columns + x];
        public bool IsWalkable(int x, int z) => x >= 0 && z >= 0 && x < columns - 1 && z < rows - 1 &&
            walkableCells[z * (columns - 1) + x] != 0;

        public bool TrySample(Vector3 position, out float worldHeight, out bool walkable)
        {
            worldHeight = 0f;
            walkable = false;
            if (!HasValidData || !float.IsFinite(position.x) || !float.IsFinite(position.z)) return false;
            var u = (position.x - worldOrigin.x) / worldSize.x;
            var v = (position.z - worldOrigin.z) / worldSize.y;
            if (u < 0f || u > 1f || v < 0f || v > 1f) return false;
            var gx = u * (columns - 1);
            var gz = v * (rows - 1);
            var x = Mathf.Min((int)gx, columns - 2);
            var z = Mathf.Min((int)gz, rows - 2);
            worldHeight = Mathf.Lerp(
                Mathf.Lerp(HeightAtVertex(x, z), HeightAtVertex(x + 1, z), gx - x),
                Mathf.Lerp(HeightAtVertex(x, z + 1), HeightAtVertex(x + 1, z + 1), gx - x), gz - z);
            walkable = IsWalkable(x, z);
            return true;
        }

        // Call once when loading a native backend, never once per unit/frame.
        public void CopyHeightsTo(float[] destination) => heights.CopyTo(destination, 0);
        public void CopyWalkabilityTo(byte[] destination) => walkableCells.CopyTo(destination, 0);

        public void SetBakedData(Vector3 origin, Vector2 size, int vertexColumns, int vertexRows,
            float slopeLimit, float[] relativeHeights, byte[] cells)
        {
            if (vertexColumns < 2 || vertexRows < 2 || !float.IsFinite(size.x) || !float.IsFinite(size.y) ||
                size.x <= 0f || size.y <= 0f || !float.IsFinite(origin.x) || !float.IsFinite(origin.y) ||
                !float.IsFinite(origin.z) || !float.IsFinite(slopeLimit) || slopeLimit < 0f || slopeLimit > 90f ||
                relativeHeights == null || cells == null ||
                relativeHeights.LongLength != (long)vertexColumns * vertexRows ||
                cells.LongLength != (long)(vertexColumns - 1) * (vertexRows - 1))
                throw new ArgumentException("Invalid map dimensions or metadata.");
            for (var i = 0; i < relativeHeights.Length; i++)
                if (!float.IsFinite(relativeHeights[i])) throw new ArgumentException("Map heights must be finite.");
            formatVersion = 1;
            worldOrigin = origin;
            worldSize = size;
            columns = vertexColumns;
            rows = vertexRows;
            maxSlopeDegrees = slopeLimit;
            // Own the arrays so changes to the baker's buffers cannot silently modify the asset.
            heights = (float[])relativeHeights.Clone();
            walkableCells = (byte[])cells.Clone();
        }

        public RecoilCastleBlockData GetCastleBlock(int index)
        {
            if (castleBlocks == null || index < 0 || index >= castleBlocks.Length)
                throw new ArgumentOutOfRangeException(nameof(index));
            return castleBlocks[index];
        }

        public void SetCastleBlocks(RecoilCastleBlockData[] blocks)
        {
            if (blocks == null || blocks.Length != 2)
                throw new ArgumentException("Exactly two castle blocks are required.", nameof(blocks));
            for (var i = 0; i < blocks.Length; i++)
            {
                var bounds = blocks[i].WorldBounds;
                var min = bounds.min;
                var max = bounds.max;
                if (!float.IsFinite(min.x) || !float.IsFinite(min.y) || !float.IsFinite(min.z) ||
                    !float.IsFinite(max.x) || !float.IsFinite(max.y) || !float.IsFinite(max.z) ||
                    bounds.size.x <= 0f || bounds.size.y <= 0f || bounds.size.z <= 0f)
                    throw new ArgumentException("Castle bounds must have finite, non-zero dimensions.", nameof(blocks));
            }
            if (blocks[0].Team == blocks[1].Team)
                throw new ArgumentException("Castle blocks must have different teams.", nameof(blocks));
            castleBlocks = (RecoilCastleBlockData[])blocks.Clone();
        }

    }
}
