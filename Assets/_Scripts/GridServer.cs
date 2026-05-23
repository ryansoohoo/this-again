using System.Collections.Generic;
using UnityEngine;

// Shared value types. Structs, alloc-free, cross client/server boundary.

public readonly struct CellData
{
    public readonly Biome Biome;
    public CellData(Biome biome) { Biome = biome; }
}

public readonly struct CellSnapshot
{
    public readonly Vector2Int Coord;
    public readonly CellData Data;
    public CellSnapshot(Vector2Int coord, CellData data) { Coord = coord; Data = data; }
}

// Server is the authoritative source of truth. Hands out the cell list as a read-only view.
// Future fog-of-war: filter Cells in the impl, or add CellAdded/CellRemoved events.
public interface IGridServer
{
    int GridSize { get; }
    IReadOnlyList<CellSnapshot> Cells { get; }
}

// Local impl: builds the full bounded grid once at construction, assigning each cell a Perlin biome.
// Deterministic + static after that.
public sealed class LocalGridServer : IGridServer
{
    public int GridSize { get; }
    public IReadOnlyList<CellSnapshot> Cells { get; }

    public LocalGridServer(int gridSize, BiomeGenerator biomes)
    {
        GridSize = gridSize;
        var list = new List<CellSnapshot>(gridSize * gridSize);
        for (int i = 0; i < gridSize; i++)
        for (int j = 0; j < gridSize; j++)
            list.Add(new CellSnapshot(new Vector2Int(i, j), new CellData(biomes.At(i, j))));
        Cells = list;
    }
}
