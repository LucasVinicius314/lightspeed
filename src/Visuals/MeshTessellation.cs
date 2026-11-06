using Godot;

namespace Lightspeed.Visuals;

/// <summary>
/// Raises the vertex density of primitive meshes so no edge is longer than a limit. The retarded-time
/// solve runs per vertex, so a long edge between two vertices stays straight even where the light delay
/// should bend it; more vertices let the shape warp. Only adds detail, never removes it.
/// </summary>
public static class MeshTessellation
{
    /// <summary>
    /// Returns a tessellated copy of <paramref name="mesh"/>; the original resource (which a ghost may
    /// share) is left untouched. Non-primitive meshes are returned as they are.
    /// </summary>
    public static Mesh Refine(Mesh mesh, float maxEdge)
    {
        if (mesh is not PrimitiveMesh)
            return mesh;

        var copy = (PrimitiveMesh)mesh.Duplicate();
        switch (copy)
        {
            case BoxMesh box:
                box.SubdivideWidth = Extra(box.Size.X, maxEdge, box.SubdivideWidth);
                box.SubdivideHeight = Extra(box.Size.Y, maxEdge, box.SubdivideHeight);
                box.SubdivideDepth = Extra(box.Size.Z, maxEdge, box.SubdivideDepth);
                break;
            case CylinderMesh cyl:
                cyl.Rings = TessellationMath.Segments(cyl.Height, maxEdge, cyl.Rings);
                cyl.RadialSegments = Around(Mathf.Max(cyl.TopRadius, cyl.BottomRadius), maxEdge, cyl.RadialSegments);
                break;
            case SphereMesh sphere:
                sphere.RadialSegments = Around(sphere.Radius, maxEdge, sphere.RadialSegments);
                sphere.Rings = TessellationMath.Segments(Mathf.Pi * sphere.Radius, maxEdge, sphere.Rings);
                break;
            case CapsuleMesh capsule:
                capsule.RadialSegments = Around(capsule.Radius, maxEdge, capsule.RadialSegments);
                capsule.Rings = TessellationMath.Segments(capsule.Height, maxEdge, capsule.Rings);
                break;
            case PlaneMesh plane: // also QuadMesh
                plane.SubdivideWidth = Extra(plane.Size.X, maxEdge, plane.SubdivideWidth);
                plane.SubdivideDepth = Extra(plane.Size.Y, maxEdge, plane.SubdivideDepth);
                break;
            case PrismMesh prism:
                prism.SubdivideWidth = Extra(prism.Size.X, maxEdge, prism.SubdivideWidth);
                prism.SubdivideHeight = Extra(prism.Size.Y, maxEdge, prism.SubdivideHeight);
                prism.SubdivideDepth = Extra(prism.Size.Z, maxEdge, prism.SubdivideDepth);
                break;
            default:
                GD.PushWarning($"{mesh.GetType().Name}: no tessellation rule, so its edges will not warp.");
                return mesh;
        }
        return copy;
    }

    /// <summary>Godot's "subdivide" counts extra cuts, so one segment means zero.</summary>
    private static int Extra(float length, float maxEdge, int currentCuts) =>
        TessellationMath.Segments(length, maxEdge, currentCuts + 1) - 1;

    private static int Around(float radius, float maxEdge, int current) =>
        TessellationMath.Segments(2f * Mathf.Pi * radius, maxEdge, current);
}
