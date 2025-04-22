# Godot Variant Shader Material

`VariantShaderMaterial` is a new custom `ShaderMaterial` type which enables the use of **shader variants.**

If you come from Unity, you may know of these already:
```hlsl
#pragma multi_compile 
#pragma shader_feature 
```

Distinctly, while Godot does have custom shader support, it does not support static branching in the form of mutable variants.

# Usage and API

To use `VariantShaderMaterial`, you must create it as a new resource just like you would an ordinary `ShaderMaterial`. It has two distinct kinds of flags.

## Terminology and Flags

* A **Feature** refers to a toggleable boolean value, like a flag. Any combination of these features can be declared, and any amount can be mixed and matched. In the inspector, these will show as different boolean options.
* An **Exclusive Variant** is a boolean value as well, **but only one exclusive variant can be selected at a time**. In the inspector, these will show in a dropdown menu.

All features go into the same pool; unlike Unity, different declarations do not contribute to separate "sets" of features (so splitting it across multiple lines is purely aesthetic and not functional).

Likewise, all exclusive variants go into the same pool; You cannot have more than one dropdown. I may change this later. For the time being, you can use features and then use the `#error "Error message"` directive in GLSL to prevent invalid usage.

!!! warning You can not place features and variants into static branches. 
    They always evaluate, and **are not subject to the preprocessor!** To disable a variant or feature, you need to comment out the line.

## In GLSL

To add variants to your shader, place the following block anywhere (but ideally near the start):
```glsl
#ifdef ADVANCED_SHADER_EXTENSIONS
// ^ This is a holdover from when I was working on an engine fork that I deemed not worth it to do.
// ^ The only thing that matters with this macro is that it evaluates to false; the code within this block will not run.

#pragma features A B C
#pragma features D E F
// ^ Again, the above two lines both contribute to the SAME POOL of features. 
// Splitting them across multiple lines is purely aesthetic and for organization.

#pragma exclusive_variants EXC0 EXC1 EXC2
#pragma exclusive_variants EXC3 EXC4 EXC5
// ^ These will show in a dropdown. Again, same pool; lines are aesthetic.

#endif


```

## In C#

To change how the shader works, use the following methods of the `VariantShaderMaterial` instance:
```cs
// Enables the provided feature flag, if it's not already enabled. If it is not recognized, an exception will be raised.
void EnableFeature(string feature);

// Disables the provided feature flag, if it's not already disabled. If it is not recognized, an exception will be raised.
void DisableFeature(string feature);

// The same as calling EnableFeature many times, but more efficient as it will not recompile after each flag is enabled.
void EnableFeatures(params string[] features);

// The same as calling DisableFeature many times, but more efficient as it will not recompile after each flag is disable.
void DisableFeatures(params string[] features);

// Changes the selected exclusive variant. A null value will default to the first defined exclusive variant.
// If you need a "no variant", it is recommended to define a variant of an underscore:
// #pragma exclusive_variants _
void SetExclusiveVariant(string? variant);

// OVERRIDES the current variant and feature list so that it exactly matches the input provided.
void SetFeaturesAndVariant(string? variant, string[] keywords);
```

## In GDScript

A GDScript implementation is not available at this time.