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
}
