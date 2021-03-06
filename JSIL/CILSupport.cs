﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Mdb;
using Mono.Cecil.Pdb;

namespace JSIL.Internal {
    public class AssemblyCache : ConcurrentCache<string, AssemblyDefinition> {
    }

    public class AssemblyResolver : BaseAssemblyResolver, IDisposable {
        private static readonly byte[] PCLPublicKeyToken = new byte[] {
            124, 236, 133, 215,
            190, 167, 121, 142
        };

        private static readonly byte[] BCLPublicKeyToken = new byte[] {
            183, 122, 92, 86,
            25, 52, 224, 137
        };

        protected readonly AssemblyCache Cache = new AssemblyCache();
        protected readonly bool OwnsCache;

        public AssemblyResolver (IEnumerable<string> dirs, AssemblyCache cache = null) {
            OwnsCache = (cache == null);
            if (cache != null)
                Cache = cache;
            else
                Cache = new AssemblyCache();

            foreach (var dir in dirs)
                AddSearchDirectory(dir);
        }

        public void Dispose () {
            if (OwnsCache)
                Cache.Dispose();
        }

        public AssemblyNameReference FilterPortableClassLibraryReferences (AssemblyNameReference name) {
            // Portable class libraries are pretty shoddily constructed. Who came up with this nonsense?

            if (!name.PublicKeyToken.SequenceEqual(PCLPublicKeyToken))
                return name;

            var bclName = new AssemblyNameReference(
                name.Name,
                // FIXME: Hard-coding 4.0 is probably a mistake
                new Version(4, 0, 0, 0)
            );

            bclName.Culture = name.Culture;
            bclName.PublicKeyToken = BCLPublicKeyToken;

            return bclName;
        }

        public override AssemblyDefinition Resolve (AssemblyNameReference name, ReaderParameters parameters) {
            if (name == null)
                throw new ArgumentNullException("name");

            var actualName = FilterPortableClassLibraryReferences(name);

            var result = Cache.GetOrCreate(actualName.FullName, (fullName) => base.Resolve(actualName, parameters));

            return result;
        }

        protected void RegisterAssembly (AssemblyDefinition assembly) {
            if (assembly == null)
                throw new ArgumentNullException("assembly");

            var name = assembly.Name.FullName;

            Cache.TryCreate(name, (fullName) => assembly);
        }
    }

    public class NoSymbolsException : FileNotFoundException {
        public NoSymbolsException (ModuleDefinition module, string fileName)
        : base(
            String.Format("Module '{0}' has no symbols.", Path.GetFileName(module.FullyQualifiedName)), fileName
        ) {
        }
    }

    public class SymbolProvider : ISymbolReaderProvider {
        public ISymbolReader GetSymbolReader (ModuleDefinition module, string fileName) {
            if (String.IsNullOrWhiteSpace(fileName))
                throw new ArgumentException("Invalid path", "fileName");

            var pdbPath = Path.Combine(
                Path.GetDirectoryName(fileName),
                Path.GetFileNameWithoutExtension(fileName) + ".pdb"
            );
            var mdbPath = Path.Combine(
                Path.GetDirectoryName(fileName),
                Path.GetFileName(fileName) + ".mdb"
            );

            if (File.Exists(pdbPath)) {
                return (new PdbReaderProvider()).GetSymbolReader(module, File.OpenRead(pdbPath));
            } else if (File.Exists(mdbPath)) {
                return (new MdbReaderProvider()).GetSymbolReader(module, fileName);
            } else {
                throw new NoSymbolsException(module, fileName);
            }
        }

        public ISymbolReader GetSymbolReader (ModuleDefinition module, Stream symbolStream) {
            // Internal constructor for no reason! Thanks!
            return (new PdbReaderProvider()).GetSymbolReader(module, symbolStream);
        }
    }

    public class CachingMetadataResolver : MetadataResolver {
        public struct Key {
            public readonly int HashCode;

            public readonly string Namespace;
            public readonly string Name;
            public readonly string DeclaringTypeName;

            public Key (TypeReference tr) {
                Namespace = tr.Namespace;
                Name = tr.Name;

                HashCode = Namespace.GetHashCode() ^ Name.GetHashCode();

                if (tr.DeclaringType != null) {
                    DeclaringTypeName = tr.DeclaringType.FullName;
                    HashCode = HashCode ^ DeclaringTypeName.GetHashCode();
                } else {
                    DeclaringTypeName = null;
                }
            }

            public bool Equals (Key rhs) {
                return (Namespace == rhs.Namespace) &&
                    (DeclaringTypeName == rhs.DeclaringTypeName) &&
                    (Name == rhs.Name);
            }

            public override bool Equals (object obj) {
                if (obj is Key) {
                    return Equals((Key)obj);
                }

                return base.Equals(obj);
            }

            public override int GetHashCode() {
                return HashCode;
            }
        }

        public class KeyComparer : IEqualityComparer<Key> {
            public bool Equals (Key x, Key y) {
                return x.Equals(y);
            }

            public int GetHashCode (Key obj) {
                return obj.HashCode;
            }
        }

        public readonly ConcurrentCache<Key, TypeDefinition> Cache;
        protected readonly ConcurrentCache<Key, TypeDefinition>.CreatorFunction<TypeReference> DoResolve;

        public CachingMetadataResolver (IAssemblyResolver assemblyResolver) :
            base(assemblyResolver) {

            Cache = new ConcurrentCache<Key, TypeDefinition>(
                Environment.ProcessorCount, 4096, new KeyComparer()
            );

            DoResolve = (key, type) => {
                var result = base.Resolve(type);

                if ((result == null) && type.FullName.StartsWith("System."))
                    Debugger.Break();

                return result;
            };
        }

        public override TypeDefinition Resolve (TypeReference type) {
            var key = new Key(type);

            var result = Cache.GetOrCreate(
                key, type, DoResolve
            );

            return result;
        }
    }

    public class FullNameAssemblyComparer : IEqualityComparer<AssemblyDefinition> {
        public bool Equals (AssemblyDefinition x, AssemblyDefinition y) {
            return x.FullName.Equals(y.FullName);
        }

        public int GetHashCode (AssemblyDefinition obj) {
            return obj.FullName.GetHashCode();
        }
    }
}
