using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

using Godot;

namespace XansGodotExtensions
{

    /// <summary>
    /// A companion class to <see cref="VariantShaderMaterial"/> which is responsible for generating the source code
    /// of variant shaders on the fly based on what variants get used.
    /// </summary>
    [Tool]
    public sealed class ShaderVariantCompiler
    {

        /// <summary>
        /// The shader in its base format, with no variants enabled. If exclusive variants are provided,
        /// and the no-variant (an underscore) isn't defined, then the first exclusive variant will
        /// be set rather than no variants.
        /// </summary>
        public Rid Basis { get; }

        /// <summary>
        /// The first or "default" exclusive variant's name.
        /// </summary>
        public string? DefaultExclusiveVariant { get; }

        private readonly string _unprocessedSource;
        private readonly List<string> _combinedFeatures = [];
        private readonly List<string> _exclusiveVariants = [];

        // n.b. future Xan: Do not free shaders from this cache. This causes errors. I assume this
        // is due to the shader cache on the driver? I wouldn't know.
        private static readonly Dictionary<string, Rid> CACHE = [];
        private bool _disposed;

        /// <summary>
        /// Asserts that the provided <paramref name="name"/> is usable as a macro in Godot shader language.
        /// </summary>
        /// <param name="name"></param>
        /// <returns></returns>
        /// <exception cref="InvalidOperationException"></exception>
        private static string AssertValidMacroName(string name)
        {
            char[] chars = name.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                char c = chars[i];
                if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9' && i != 0) || (c == '_'))
                {
                    continue;
                }
                throw new InvalidOperationException($"Invalid variant name: {name}");
            }
            return name;
        }

        /// <summary>
        /// An awful and slow hack to preprocess shader source code. This is used because
        /// <see cref="RenderingServer.ShaderSetCode(Rid, string)"/> does NOT go through
        /// Godot's preprocessor (but <see cref="Shader.Code"/> does), so having any
        /// comments or preprocessor statements will cause it to error.
        /// </summary>
        /// <param name="shaderSource"></param>
        /// <returns></returns>
        public static string Preprocess(string shaderSource)
        {
            Shader shader = new Shader();
            shader.Code = shaderSource;
            string code = RenderingServer.ShaderGetCode(shader.GetRid());
            return code;
        }

        /// <summary>
        /// Wrap the provided shader in a recompiler for all of its variants.
        /// </summary>
        /// <param name="shaderSource"></param>
        public ShaderVariantCompiler(string shaderSource)
        {
            _unprocessedSource = shaderSource;
            Basis = RenderingServer.ShaderCreate();
            RenderingServer.ShaderSetCode(Basis, Preprocess(shaderSource));

            StringBuilder code = new StringBuilder();
            List<string> features = [];
            List<string> singles = [];
            using (TextReader text = new StringReader(shaderSource))
            {
                do
                {
                    string? line = text.ReadLine();
                    if (line == null) break;

                    int indexOffset = 0;
                    while (indexOffset < line.Length && char.IsWhiteSpace(line[indexOffset]))
                    {
                        indexOffset++;
                    }
                    if (line.IndexOf("#pragma features ", indexOffset) == indexOffset)
                    {
                        features.AddRange(line[("#pragma features ".Length + indexOffset)..].Replace('\t', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(AssertValidMacroName));

                    }
                    else if (line.IndexOf("#pragma exclusive_variants ", indexOffset) == indexOffset)
                    {
                        singles.AddRange(line[("#pragma exclusive_variants ".Length + indexOffset)..].Replace('\t', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(AssertValidMacroName));

                    }
                    else if (line.IndexOf("#include ", indexOffset) == indexOffset)
                    {
                        string path = line[("#include ".Length + indexOffset)..];
                        if ((path[0] == '"' && path[^1] == '"') || (path[0] == '\'' && path[^1] == '\''))
                        {
                            path = path[1..^2];
                        }
                        if (!path.StartsWith("res://", StringComparison.OrdinalIgnoreCase))
                        {
                            throw new NotSupportedException("When using #include statements in a variant shader, it MUST use an absolute path beginning with res://");
                        }
                    }

                    code.AppendLine(line);
                } while (true);
            }
            _combinedFeatures = new List<string>(features);
            _exclusiveVariants = new List<string>(singles);
            DefaultExclusiveVariant = singles.FirstOrDefault();
        }

        /// <summary>
        /// Provides an ordered list of every option and every exclusive variant to the caller,
        /// so that it may be added to a properties list.
        /// </summary>
        /// <param name="features"></param>
        /// <param name="exclusives"></param>
        public void GetAllShaderOptions(out string[] features, out string[] exclusives)
        {
            features = _combinedFeatures.ToArray();
            exclusives = _exclusiveVariants.ToArray();
        }

        /// <summary>
        /// Returns an exclusive index by index, or null if the index is invalid.
        /// </summary>
        /// <param name="index"></param>
        /// <returns></returns>
        /// <exception cref="ArgumentOutOfRangeException"></exception>
        /// <exception cref="IndexOutOfRangeException"></exception>
        /// <exception cref="ObjectDisposedException">This variant compiler has been destroyed.</exception>
        public string? GetExclusiveByIndex(int index)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            if (index > _exclusiveVariants.Count) return null;
            return _exclusiveVariants.ToArray()[index];
        }

        /// <summary>
        /// Returns true if the provided keyword is a member of this shader.
        /// </summary>
        /// <param name="keyword"></param>
        /// <returns></returns>
        /// <exception cref="ObjectDisposedException">This variant compiler has been destroyed.</exception>
        public bool IsValidFeature(string keyword)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _combinedFeatures.Contains(keyword);
        }

        /// <summary>
        /// Returns true if the provided exclusive keyword is a member of this shader.
        /// </summary>
        /// <param name="variant"></param>
        /// <returns></returns>
        /// <exception cref="ObjectDisposedException">This variant compiler has been destroyed.</exception>
        public bool IsValidExclusive(string? variant)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return variant == null || _exclusiveVariants.Contains(variant);
        }

        /// <summary>
        /// Returns the version of the shader that has the provided <paramref name="flags"/> enabled.
        /// </summary>
        /// <param name="flags"></param>
        /// <param name="singleExtra"></param>
        /// <returns></returns>
        /// <exception cref="ObjectDisposedException">This variant compiler has been destroyed.</exception>
        public Rid GetShaderWithFlags(IEnumerable<string> flags, string? singleExtra, bool ignoreInvalid = false)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (!flags.Any() && singleExtra == null)
            {
                return Basis;
            }

            lock (CACHE)
            {
                string src = CreateShaderSourceWithFlags(flags, singleExtra, ignoreInvalid);
                string hash = BitConverter.ToString(MD5.HashData(Encoding.UTF8.GetBytes(src)));
                if (CACHE.TryGetValue(hash, out Rid shader))
                {
                    return shader;
                }

                shader = RenderingServer.ShaderCreate();
                RenderingServer.ShaderSetCode(shader, Preprocess(src));
                return CACHE[hash] = shader;
            }
        }

        /// <summary>
        /// Writes the code out for a shader that includes the given flags.
        /// </summary>
        /// <param name="flags"></param>
        /// <param name="singleExtra">The variant to use, or null for the default (first) variant.</param>
        /// <param name="ignoreInvalid">If an invalid variant or extra is input, ignore it instead of raising an exception</param>
        /// <returns></returns>
        /// <exception cref="InvalidOperationException">One or more features are invalid, or the variant is invalid.</exception>
        private string CreateShaderSourceWithFlags(IEnumerable<string> flags, string? singleExtra, bool ignoreInvalid)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            string code = _unprocessedSource;
            singleExtra ??= DefaultExclusiveVariant;

            if (!flags.Any() && singleExtra == null)
            {
                return code;
            }

            StringBuilder result = new StringBuilder();
            foreach (string toggle in flags)
            {
                if (_combinedFeatures.Contains(toggle))
                {
                    result.Append("#define ");
                    result.AppendLine(toggle);
                }
                else
                {
                    if (!ignoreInvalid) throw new InvalidOperationException($"No such feature {toggle}");
                }
            }
            if (singleExtra != null)
            {
                if (_exclusiveVariants.Contains(singleExtra))
                {
                    result.Append("#define ");
                    result.AppendLine(singleExtra);
                }
                else
                {
                    if (!ignoreInvalid) throw new InvalidOperationException($"No such variant {singleExtra}");
                }
            }
            result.AppendLine();
            result.Append(code);
            return result.ToString();
        }

    }
}
