namespace MassiveSlicer.Viewport;

/// <summary>Viewport material/shading mode selected from the toolbar.</summary>
public enum ShaderMode
{
    /// <summary>Uses each mesh's material colour extracted from the source file (default).</summary>
    Standard,
    /// <summary>Overrides all materials with a flat warm-clay appearance.</summary>
    Clay,
    /// <summary>Overrides all materials with a metallic appearance and Fresnel rim.</summary>
    Metal,
    /// <summary>Overrides all materials with a highly reflective chrome finish.</summary>
    Chrome,
    /// <summary>Overrides all materials with a flat matte-black finish.</summary>
    MatteBlack,
    /// <summary>Overrides all materials with a purple shiny finish.</summary>
    Purple,
    /// <summary>Renders world-space normals as RGB colour (X->R, Y->G, Z->B remapped to 0-1).</summary>
    Normals,

    // -- Material-channel debug views (inspector). Show the raw PBR channel of the selected
    //    mesh; cell geometry stays in the cheap fast-cell path. --
    /// <summary>Base colour (albedo) map / factor only.</summary>
    BaseColor,
    /// <summary>Metalness channel as greyscale.</summary>
    Metalness,
    /// <summary>Roughness channel as greyscale.</summary>
    Roughness,
    /// <summary>Tangent-space normal map (or geometric normal when absent).</summary>
    NormalMap,
    /// <summary>Ambient-occlusion channel as greyscale.</summary>
    AO,
    /// <summary>Emissive map / factor.</summary>
    Emission,
    /// <summary>Procedural UV checker (magenta where UVs are missing).</summary>
    UvChecker,
}
