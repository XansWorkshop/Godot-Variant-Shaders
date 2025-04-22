shader_type spatial;

#ifdef ADVANCED_SHADER_FEATURES_FORK
// ^ This is purely for formatting. The fork adds the following lines as valid lines:

#pragma features HALF_BRIGHTNESS UNSHADED
#pragma exclusive_variants NONE RED GREEN BLUE

#endif

#ifdef UNSHADED
render_mode unshaded;
#endif

void fragment() {
	// Called for every pixel the material is visible on.
	vec3 color = vec3(0);
	#if defined(RED)
		color = vec3(1.0, 0.0, 0.0);
	#elif defined(GREEN)
		color = vec3(0.0, 1.0, 0.0);
	#elif defined(BLUE)
		color = vec3(0.0, 0.0, 1.0);
	#endif

	#ifdef HALF_BRIGHTNESS
		color *= 0.25;
	#endif

	ALBEDO = color;
}
