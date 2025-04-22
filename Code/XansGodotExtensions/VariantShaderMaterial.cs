using System;
using System.Collections.Generic;
using System.Threading;

using Godot;
using Godot.Collections;

namespace XansGodotExtensions
{

    /// <summary>
    /// A shader <see cref="Material"/> that supports static variants (or "keywords") via <see cref="ShaderVariantCompiler"/>.
    /// </summary>
    [Tool, GlobalClass]
    public partial class VariantShaderMaterial : ShaderMaterial
    {
        private Shader? _originalShaderSetByUser;
        private readonly ReaderWriterLockSlim _userShaderInstanceLock = new ReaderWriterLockSlim(LockRecursionPolicy.SupportsRecursion);
        private readonly SortedSet<string> _currentFeatures = [];
        private ShaderVariantCompiler? _variants;
        private string? _singleExtra = null;
        private Rid _expectedShader;
        private string? _shaderSource;

        private bool IsReady => _variants != null;

        /// <summary>
        /// A method which prepares a shader to listen for a change to its <see cref="Shader.Code"/> property.
        /// When invoked, the event disconnects itself and then calls <see cref="SetToShader(Shader?)"/> which
        /// reconnects this code on whatever the new shader is (even if it's the same instance).
        /// </summary>
        /// <param name="shader"></param>
        private void ListenForShaderCodeChange(Shader shader)
        {
            void _onChanged()
            {
                if (!IsInstanceValid(shader))
                {
                    return; // Caused by rebuilding or destruction.
                }
                shader.Changed -= _onChanged;
                if (shader != null)
                {
                    _shaderSource = shader.Code;
                    SetToShader(shader); // Set the shader to itself to update when the code is changed.
                }
            }
            shader.Changed += _onChanged;
        }

        /// <summary>
        /// Removes all whitespace from the provided string, determined by <see cref="char.IsWhiteSpace(char)"/>
        /// </summary>
        /// <param name="text"></param>
        /// <returns></returns>
        private static string FastRemoveWhitespace(string text)
        {
            if (text.Length == 0) return text;

            int writeHead = 0;
            int readHead = 0;
            int length = text.Length;
            char[] buffer = text.ToCharArray();
            do
            {
                char buf = buffer[readHead++];
                if (!char.IsWhiteSpace(buf))
                {
                    buffer[writeHead++] = buf;
                }
            } while (readHead < length);
            return new string(buffer, 0, writeHead);
        }

        /// <inheritdoc/>
        public override Shader.Mode _GetShaderMode()
        {
            if (!_expectedShader.IsValid) return default;

            // This is a really weird solution and I don't even know if it's valid.
            string code = RenderingServer.ShaderGetCode(_expectedShader);
            int shaderType = code.Find("shader_type");
            int ending = code.Find(';', shaderType);
            if (shaderType >= 0 && ending > 0)
            {
                string contents = code.Substring(shaderType + "shader_type".Length, ending - shaderType).Replace(";", null);
                contents = FastRemoveWhitespace(contents);
                return contents switch
                {
                    "spatial" => Shader.Mode.Spatial,
                    "canvas_item" => Shader.Mode.CanvasItem,
                    "particles" => Shader.Mode.Particles,
                    "sky" => Shader.Mode.Sky,
                    "fog" => Shader.Mode.Fog,
                    _ => throw new InvalidOperationException($"Unknown shader type {contents}")
                };
            }
            else
            {
                throw new InvalidOperationException($"Unknown shader type (unable to find shader_type directive).");
            }
        }

        /// <inheritdoc/>
        public override Rid _GetShaderRid()
        {
            _userShaderInstanceLock.EnterReadLock();
            try
            {
                if (!IsReady) return default;
                return _expectedShader;
            }
            finally
            {
                _userShaderInstanceLock.ExitReadLock();
            }
        }

        /// <summary>
        /// Changes the base shader used that all variants branch off of.
        /// </summary>
        /// <param name="shader"></param>
        private void SetToShader(Shader? shader)
        {
            _userShaderInstanceLock.EnterWriteLock();
            try
            {
                bool canKeepFeatures = true;
                _variants = null;

                if (!canKeepFeatures)
                {
                    _currentFeatures.Clear();
                    _singleExtra = null;
                }
                _shaderSource = null;

                if (shader != null)
                {
                    Shader = shader;
                    _originalShaderSetByUser = shader;
                    _shaderSource = shader.Code;
                    _variants = new ShaderVariantCompiler(_shaderSource);

                    _expectedShader = _variants.GetShaderWithFlags(_currentFeatures, _singleExtra, canKeepFeatures);
                    RenderingServer.MaterialSetShader(GetRid(), _expectedShader);

                    ListenForShaderCodeChange(shader);
                }
                else
                {
                    Shader = null;
                    _originalShaderSetByUser = null;
                    _currentFeatures.Clear();
                    _singleExtra = null;
                    RenderingServer.MaterialSetShader(GetRid(), default);
                }
            }
            finally
            {
                _userShaderInstanceLock.ExitWriteLock();
            }
        }

        /// <summary>
        /// Updates the specific compiled variation of this shader that is in use.
        /// </summary>
        /// <exception cref="InvalidOperationException"></exception>
        private void UpdateExpectedShader()
        {
            _userShaderInstanceLock.EnterReadLock();
            try
            {
                if (!IsReady) throw new InvalidOperationException("No shader has been set.");

                _expectedShader = _variants!.GetShaderWithFlags(_currentFeatures, _singleExtra);
                RenderingServer.MaterialSetShader(GetRid(), _expectedShader);
            }
            finally
            {
                _userShaderInstanceLock.ExitReadLock();
            }
        }

        /// <summary>
        /// Switches the shader to one where the provided feature is enabled.
        /// If more than one feature needs to change, consider using <see cref="EnableFeatures(string[])"/> or <see cref="ManyFeatureChanges(Action{ShaderMutator})"/>
        /// </summary>
        /// <param name="feature"></param>
        public void EnableFeature(string feature)
        {
            _userShaderInstanceLock.EnterReadLock();
            try
            {
                if (!IsReady) throw new InvalidOperationException("No shader has been set.");

                if (_variants!.IsValidFeature(feature))
                {
                    if (_currentFeatures.Add(feature))
                    {
                        UpdateExpectedShader();
                    }
                }
                else
                {
                    throw new ArgumentException($"The provided feature {feature} is not defined on this shader.");
                }
            }
            finally
            {
                _userShaderInstanceLock.ExitReadLock();
            }
        }

        /// <summary>
        /// Switches the shader to one where the provided feature is disabled.
        /// If more than one feature needs to change, consider using <see cref="DisableFeatures(string[])"/> or <see cref="ManyFeatureChanges(Action{ShaderMutator})"/>
        /// </summary>
        /// <param name="feature"></param>
        public void DisableFeature(string feature)
        {
            _userShaderInstanceLock.EnterReadLock();
            try
            {
                if (!IsReady) throw new InvalidOperationException("No shader has been set.");

                if (_variants!.IsValidFeature(feature))
                {
                    if (_currentFeatures.Remove(feature))
                    {
                        UpdateExpectedShader();
                    }
                }
                else
                {
                    throw new ArgumentException($"The provided feature {feature} is not defined on this shader.");
                }
            }
            finally
            {
                _userShaderInstanceLock.ExitReadLock();
            }
        }
        /// <summary>
        /// Enables several features at once.
        /// </summary>
        /// <param name="features"></param>
        public void EnableFeatures(params string[] features)
        {
            _userShaderInstanceLock.EnterReadLock();
            try
            {
                if (!IsReady) throw new InvalidOperationException("No shader has been set.");

                bool update = false;
                foreach (string feature in features)
                {
                    if (_variants!.IsValidFeature(feature))
                    {
                        if (_currentFeatures.Add(feature))
                        {
                            update = true;
                        }
                    }
                    else
                    {
                        throw new ArgumentException($"The provided feature {feature} is not defined on this shader.");
                    }
                }
                if (update) UpdateExpectedShader();
            }
            finally
            {
                _userShaderInstanceLock.ExitReadLock();
            }
        }

        /// <summary>
        /// Disables several features at once.
        /// </summary>
        /// <param name="features"></param>
        public void DisableFeatures(params string[] features)
        {
            _userShaderInstanceLock.EnterReadLock();
            try
            {
                if (!IsReady) throw new InvalidOperationException("No shader has been set.");

                bool update = false;
                foreach (string feature in features)
                {
                    if (_variants!.IsValidFeature(feature))
                    {
                        if (_currentFeatures.Remove(feature))
                        {
                            update = true;
                        }
                    }
                    else
                    {
                        throw new ArgumentException($"The provided feature {feature} is not defined on this shader.");
                    }
                }
                if (update) UpdateExpectedShader();
            }
            finally
            {
                _userShaderInstanceLock.ExitReadLock();
            }
        }

        /// <summary>
        /// Sets the variant to one of the exclusive variants of this shader, by name.
        /// </summary>
        /// <param name="variant"></param>
        public void SetExclusiveVariant(string? variant)
        {
            _userShaderInstanceLock.EnterReadLock();
            try
            {
                if (!IsReady) throw new InvalidOperationException("No shader has been set.");

                if (_variants!.IsValidExclusive(variant))
                {
                    if (_singleExtra != variant)
                    {
                        _singleExtra = variant;
                        UpdateExpectedShader();
                    }
                }
                else
                {
                    throw new ArgumentException($"The provided exclusive variant {variant} is not defined on this shader.");
                }
            }
            finally
            {
                _userShaderInstanceLock.ExitReadLock();
            }
        }

        /// <summary>
        /// Set a variant and all features in a single call, to reduce overhead from changes.
        /// <para/>
        /// This overrides the state to the provided values.
        /// </summary>
        /// <param name="variant"></param>
        /// <param name="keywords"></param>
        public void SetFeaturesAndVariant(string? variant, string[] keywords)
        {
            _userShaderInstanceLock.EnterReadLock();
            try
            {
                if (!IsReady) throw new InvalidOperationException("No shader has been set.");

                if (variant == null)
                {
                    _singleExtra = null;
                }
                else
                {
                    _singleExtra = _variants!.IsValidExclusive(variant) ? variant : throw new ArgumentException($"The provided exclusive variant {variant} is not defined on this shader.");
                }
                _currentFeatures.Clear();
                foreach (string keyword in keywords)
                {
                    if (_variants!.IsValidFeature(keyword))
                    {
                        _currentFeatures.Add(keyword);
                    }
                    else
                    {
                        throw new ArgumentException($"The provided keyword {keyword} is not defined on this shader.");
                    }
                }
                UpdateExpectedShader();
            }
            finally
            {
                _userShaderInstanceLock.ExitReadLock();
            }
        }

        /// <summary>
        /// Allows a delegate to perform changes to many keywords on this shader without applying until all changes are done.
        /// </summary>
        /// <returns></returns>
        public void ManyFeatureChanges(Action<ShaderMutator> change)
        {
            _userShaderInstanceLock.EnterReadLock();
            try
            {
                if (!IsReady) throw new InvalidOperationException("No shader has been set.");
                ArgumentNullException.ThrowIfNull(change);
                using ShaderMutator mutator = new ShaderMutator(this);
                change(mutator);
            }
            finally
            {
                _userShaderInstanceLock.ExitReadLock();
            }
        }

        private static readonly StringName SHADER_PROPERTY_NAME = ShaderMaterial.PropertyName.Shader;
        private static readonly StringName VARIANT_PROPERTY_NAME = "variant";
        private static readonly StringName ORIGINAL_SHADER_PROPERTY_NAME = "original";

        /// <inheritdoc/>
        public override Array<Dictionary> _GetPropertyList()
        {
            Array<Dictionary> props = [
                new Dictionary {
                    { "name", ORIGINAL_SHADER_PROPERTY_NAME },
                    { "type", (int)Variant.Type.Object },
                    { "hint", (int)PropertyHint.ResourceType },
                    { "hint_string", nameof(Godot.Shader) },
                    { "usage", (int)PropertyUsageFlags.NoEditor }
                }
            ];
            if (IsReady)
            {
                _variants!.GetAllShaderOptions(out string[] features, out string[] exclusives);
                foreach (string feature in features)
                {
                    props.Add(new Dictionary {
                        { "name", feature },
                        { "type", (int)Variant.Type.Bool }
                    });
                }
                if (exclusives.Length > 0)
                {
                    string exclusivesStr = string.Join(',', exclusives);
                    props.Add(new Dictionary {
                        { "name", VARIANT_PROPERTY_NAME },
                        { "type", (int)Variant.Type.String },
                        { "hint", (int)PropertyHint.Enum },
                        { "hint_string", exclusivesStr }
                    });
                }
            }
            return props;
        }

        /// <inheritdoc/>
        public override Variant _Get(StringName property)
        {
            if (property == SHADER_PROPERTY_NAME)
            {
                return Shader!;
            }
            else if (property == ORIGINAL_SHADER_PROPERTY_NAME)
            {
                return _originalShaderSetByUser!;
            }
            else
            {
                string propStr = property;
                if (IsReady)
                {
                    if (_variants!.IsValidFeature(propStr))
                    {
                        return _currentFeatures.Contains(propStr);
                    }
                    else if (property == VARIANT_PROPERTY_NAME)
                    {
                        if (_singleExtra == null)
                        {
                            return _variants!.DefaultExclusiveVariant!;
                        }
                        else
                        {
                            return _singleExtra;
                        }
                    }
                }
            }
            return default;
        }

        /// <inheritdoc/>
        public override bool _Set(StringName property, Variant value)
        {
            if (property == SHADER_PROPERTY_NAME || property == ORIGINAL_SHADER_PROPERTY_NAME)
            {
                if (value.VariantType == Variant.Type.Object)
                {
                    if (value.Obj is Shader shader)
                    {
                        _originalShaderSetByUser = shader;
                        SetToShader(shader);
                    }
                    else if (value.Obj is null)
                    {
                        _originalShaderSetByUser = null;
                        SetToShader(null);
                    }
                    NotifyPropertyListChanged();
                    return true;
                }

            }
            else
            {
                string propStr = property;

                if (!IsReady)
                {
                    // This ensures that the data is loaded ahead of time, so that
                    // setting the parameters works.
                    Shader? shader = _originalShaderSetByUser;
                    if (shader != null)
                    {
                        SetToShader(shader);
                    }
                }

                if (IsReady)
                {
                    if (_variants!.IsValidFeature(propStr))
                    {
                        if (value.VariantType != Variant.Type.Bool) return false;
                        bool isOn = (bool)value;
                        if (isOn)
                        {
                            EnableFeature(propStr);
                        }
                        else
                        {
                            DisableFeature(propStr);
                        }
                        NotifyPropertyListChanged();
                        return true;
                    }
                    else if (property == VARIANT_PROPERTY_NAME)
                    {
                        if (value.VariantType != Variant.Type.String && value.VariantType != Variant.Type.Nil) return false;
                        string? variant = (string?)value;
                        if (_variants!.IsValidExclusive(variant))
                        {
                            SetExclusiveVariant(variant);
                        }
                        NotifyPropertyListChanged();
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>
        /// This struct can be used to enable or disable keywords without affecting shader state.
        /// </summary>
        public class ShaderMutator : IDisposable
        {

            private bool _lockedFromDisposal = false;

            private readonly List<string> _toEnable = [];
            private readonly List<string> _toDisable = [];
            private readonly VariantShaderMaterial _mtl;

            internal ShaderMutator(VariantShaderMaterial mtl)
            {
                _mtl = mtl;
            }

            /// <summary>
            /// Enable the provided keyword.
            /// </summary>
            /// <param name="feature"></param>
            public void EnableFeature(string feature)
            {
                ObjectDisposedException.ThrowIf(_lockedFromDisposal, this);
                int existing = _toDisable.IndexOf(feature);
                if (existing > -1)
                {
                    _toDisable.Remove(feature);
                }
                _toEnable.Add(feature);
            }

            /// <summary>
            /// Disable the provided keyword.
            /// </summary>
            /// <param name="feature"></param>
            public void DisableFeature(string feature)
            {
                ObjectDisposedException.ThrowIf(_lockedFromDisposal, this);
                int existing = _toEnable.IndexOf(feature);
                if (existing > -1)
                {
                    _toEnable.Remove(feature);
                }
                _toDisable.Add(feature);
            }

            internal void Apply()
            {
                ObjectDisposedException.ThrowIf(_lockedFromDisposal, this);
                _lockedFromDisposal = true;

                bool update = false;
                foreach (string feature in _toEnable)
                {
                    if (_mtl._variants!.IsValidFeature(feature))
                    {
                        if (_mtl._currentFeatures.Add(feature))
                        {
                            update = true;
                        }
                    }
                }
                foreach (string feature in _toDisable)
                {
                    if (_mtl._variants!.IsValidFeature(feature))
                    {
                        if (_mtl._currentFeatures.Remove(feature))
                        {
                            update = true;
                        }
                    }
                }
                if (update) _mtl.UpdateExpectedShader();
            }

            void IDisposable.Dispose()
            {
                GC.SuppressFinalize(this);
                Apply();
            }
        }
    }
}