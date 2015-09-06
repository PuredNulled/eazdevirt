﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using dnlib.DotNet;
using dnlib.DotNet.MD;

namespace eazdevirt.IO
{
	public partial class Resolver : ResourceReader
	{
		/// <summary>
		/// Logger.
		/// </summary>
		public ILogger Logger { get; private set; }

		/// <summary>
		/// Lock used for all public resolve methods.
		/// </summary>
		private Object _lock = new Object();

		/// <summary>
		/// AssemblyResolver.
		/// </summary>
		private AssemblyResolver _asmResolver;

		public Resolver(EazModule module, ILogger logger)
			: base(module)
		{
			this.Logger = (logger != null ? logger : DummyLogger.NoThrowInstance);
			this.InitializeAssemblyResolver();
		}

		private void InitializeAssemblyResolver()
		{
			_asmResolver = new AssemblyResolver();
		}

		/// <summary>
		/// Resolve a user string.
		/// </summary>
		/// <param name="position">Position</param>
		/// <returns>String</returns>
		public String ResolveString(Int32 position)
		{
			lock (_lock)
			{
				return this.ResolveString_NoLock(position);
			}
		}

		String ResolveString_NoLock(Int32 position)
		{
			this.Stream.Position = position;

			InlineOperand operand = new InlineOperand(this.Reader);
			if (operand.IsToken)
			{
				return this.Module.ReadUserString((UInt32)operand.Token);
			}
			else
			{
				StringData data = operand.Data as StringData;
				return data.Value;
			}
		}

		/// <summary>
		/// Resolve a method.
		/// </summary>
		/// <param name="position">Position</param>
		/// <returns>IMethod</returns>
		public IMethod ResolveMethod(Int32 position)
		{
			lock(_lock)
			{
				return this.ResolveMethod_NoLock(position);
			}
		}

		/// <summary>
		/// Try to resolve a method without throwing.
		/// </summary>
		/// <param name="position">Position</param>
		/// <returns>IMethod, or null if unable to resolve</returns>
		public IMethod TryResolveMethod(Int32 position)
		{
			try
			{
				return this.ResolveMethod(position);
			}
			catch(Exception)
			{
				return null;
			}
		}

		IMethod ResolveMethod_NoLock(TypeDef declaringDef, MethodData data)
		{
			// If declaring type is a TypeDef, it is defined inside this module, so a
			// MethodDef should be attainable. However, the method may have generic parameters.

			// This signature may not be exact:
			// If `MyMethod<T>(T something): Void` is called as MyMethod<Int32>(100),
			// signature will appear as `System.Void <!!0>(System.Int32)` instead of `System.Void <!!0>(T)`
			var sig = GetMethodSig(data);

			// Try to easily find the method by name + signature
			var foundMethod = declaringDef.FindMethod(data.Name, sig);
			if (foundMethod != null)
				return foundMethod;

			var methods = declaringDef.FindMethods(data.Name);
			foreach (var method in methods)
			{
				if (method.IsStatic != data.IsStatic)
					continue;

				if (Matches(method, sig))
				{
					// This should only happen for generic inst methods
					return ToMethodSpec(method, data);
				}
			}

			throw new Exception("[ResolveMethod_NoLock] Unable to resolve method from declaring TypeDef");
		}

		IMethod ResolveMethod_NoLock(TypeRef declaringRef, MethodData data)
		{
			MethodSig methodSig = GetMethodSig(data);
			MemberRef memberRef = new MemberRefUser(this.Module, data.Name, methodSig, declaringRef);
			return memberRef;
		}

		IMethod ResolveMethod_NoLock(TypeSpec declaringSpec, MethodData data)
		{
			// If declaring type is a TypeSpec, it should have generic types associated
			// with it. This doesn't mean the method itself will, though.

			var comparer = new SignatureEqualityComparer(SigComparerOptions.SubstituteGenericParameters);
			MethodSig methodSig = GetMethodSig(data);

			// TEST
			//var allAssemblyRefs = this.Module.GetAssemblyRefs();
			//this.Logger.Info(this, "Module count: {0}", allAssemblyRefs.Count());
			//foreach (var a in allAssemblyRefs)
			//{
			//	this.Logger.Info(this, " {0}", a);
			//
			//	AssemblyResolver resolver = new AssemblyResolver();
			//	var assembly = resolver.Resolve(a, this.Module);
			//	var module = assembly.ManifestModule;
			//
			//	this.Logger.Info(this, " Types: {0}", module.Types.Count);
			//}
			// END TEST

			if (declaringSpec.TypeSig.IsGenericInstanceType)
			{
				this.Logger.Verbose(this, "Comparing against possible method sigs: {0}", data.Name);

				//TypeDef declaringDef = declaringSpec.ResolveTypeDefThrow();
				TypeDef declaringDef = ResolveTypeDefThrow(declaringSpec);
				AssemblyRef assemblyRef = this.Module.GetAssemblyRefs().First((mr) => {
					return (mr.FullName.Equals(declaringDef.Module.Assembly.FullName));
				});

				TypeRef declaringRef = new TypeRefUser(this.Module,
					declaringDef.Namespace, declaringDef.Name, assemblyRef);

				Console.WriteLine("declaringRef: {0}", declaringRef);

				var methods = declaringDef.FindMethods(data.Name);
				var possibleMethodSigs = CreateMethodSigs(declaringSpec, methodSig, data);

				foreach (var possibleMethodSig in possibleMethodSigs)
					this.Logger.Verbose(this, "Possible method sig: {0}", possibleMethodSig.ToString());

				foreach (var possibleMethodSig in possibleMethodSigs)
				{
					MethodDef found = declaringDef.FindMethod(data.Name, possibleMethodSig);
					//MemberRef foundRef = new MemberRefUser(this.Module, found.Name, found.MethodSig, declaringRef);

					if (found != null)
					{
						this.Logger.Verbose(this, "Signature match: {0}", possibleMethodSig.ToString());

						var hasMvar = found.MethodSig.Params.Any((p) => {
							return p.IsGenericMethodParameter;
						});

						if (hasMvar)
						{
							return ToMethodSpec(found, data);
							//MemberRef memberRef = new MemberRefUser(this.Module, found.Name, found.MethodSig, declaringRef);
							//return ToMethodSpec(memberRef, data);
						}
						else
						{
							var memberRef = new MemberRefUser(this.Module, found.Name, possibleMethodSig, declaringSpec);
						}
					}
				}

				//foreach (var method in methods)
				//{
				//	if (Equals(declaringSpec, method.MethodSig, methodSig))
				//	{
				//		return ToMethodSpec(
				//			new MemberRefUser(this.Module, data.Name, method.MethodSig, declaringSpec),
				//			data);
				//	}
				//}
			}

			throw new Exception(String.Format(
				"[ResolveMethod_NoLock] Unable to resolve {0} method from declaring TypeSpec {1}",
				data.Name, declaringSpec.ToString()));

			//if (data.HasGenericArguments)
			//{
			//	MemberRef memberRef = new MemberRefUser(this.Module, data.Name, methodSig, declaringSpec);
			//	return ToMethodSpec(memberRef, data);
			//}
			//else
			//{
			//	MemberRef memberRef = new MemberRefUser(this.Module, data.Name, methodSig, declaringSpec);
			//	return memberRef;
			//}
		}

		IMethod ResolveMethod_NoLock(Int32 position)
		{
			this.Stream.Position = position;

			InlineOperand operand = new InlineOperand(this.Reader);
			if(operand.IsToken)
			{
				MDToken token = new MDToken(operand.Token);
				if (token.Table == Table.Method)
					return this.Module.ResolveMethod(token.Rid);
				else if (token.Table == Table.MemberRef)
					return this.Module.ResolveMemberRef(token.Rid);
				else if (token.Table == Table.MethodSpec)
					return this.Module.ResolveMethodSpec(token.Rid);

				throw new Exception("[ResolveMethod_NoLock] Bad MDToken table");
			}
			else
			{
				MethodData data = operand.Data as MethodData;
				ITypeDefOrRef declaring = this.ResolveType_NoLock(data.DeclaringType.Position);

				if (declaring is TypeDef)
				{
					TypeDef declaringDef = declaring as TypeDef;
					return this.ResolveMethod_NoLock(declaringDef, data);
				}
				else if (declaring is TypeRef)
				{
					TypeRef declaringRef = declaring as TypeRef;
					return this.ResolveMethod_NoLock(declaringRef, data);
				}
				else if (declaring is TypeSpec)
				{
					TypeSpec declaringSpec = declaring as TypeSpec;
					return this.ResolveMethod_NoLock(declaringSpec, data);
				}

				throw new Exception("[ResolveMethod_NoLock] Expected declaring type to be a TypeDef, TypeRef or TypeSpec");
			}
		}

		TypeDef ResolveTypeDefThrow(TypeSpec typeSpec)
		{
			var assembly = _asmResolver.ResolveThrow(typeSpec.DefinitionAssembly, this.Module);
			var module = assembly.ManifestModule;
			var name = FixTypeSpecName(typeSpec.FullName);

			var type = module.Find(name, false);
			if (type == null)
				throw new Exception(String.Format(
					"Unable to find TypeDef in module: {0}", name));

			return type;
		}

		String FixTypeSpecName(String fullName)
		{
			Regex regex = new Regex("\\<[^\\<]+\\>");
			String newName = fullName;

			while (true)
			{
				newName = regex.Replace(fullName, "");
				if (newName.Equals(fullName))
					break;

				fullName = newName;
			}

			return newName;
		}

		/// <summary>
		/// Resolve a field.
		/// </summary>
		/// <param name="position">Position</param>
		/// <returns>Field</returns>
		public IField ResolveField(Int32 position)
		{
			lock (_lock)
			{
				return this.ResolveField_NoLock(position);
			}
		}

		public IField TryResolveField(Int32 position)
		{
			try
			{
				return this.ResolveField(position);
			}
			catch (Exception)
			{
				return null;
			}
		}

		IField ResolveField_NoLock(Int32 position)
		{
			this.Stream.Position = position;

			InlineOperand operand = new InlineOperand(this.Reader);
			if (operand.IsToken)
			{
				MDToken token = new MDToken(operand.Token);
				return this.Module.ResolveField(token.Rid);
			}
			else
			{
				FieldData data = operand.Data as FieldData;
				ITypeDefOrRef type = this.ResolveType_NoLock(data.FieldType.Position);
				if (type == null)
					throw new Exception("[ResolveField_NoLock] Unable to resolve type as TypeDef or TypeRef");

				TypeDef typeDef = type as TypeDef;
				if (typeDef != null)
					return typeDef.FindField(data.Name);

				typeDef = (type as TypeRef).ResolveTypeDef();
				if (typeDef != null)
					return typeDef.FindField(data.Name);

				throw new Exception("[ResolveField_NoLock] Currently unable to resolve a field from an unresolvable TypeRef");
			}
		}

		/// <summary>
		/// Resolve a type.
		/// </summary>
		/// <param name="position">Position</param>
		/// <returns>Type</returns>
		public ITypeDefOrRef ResolveType(Int32 position)
		{
			lock (_lock)
			{
				return this.ResolveType_NoLock(position);
			}
		}

		public ITypeDefOrRef TryResolveType(Int32 position)
		{
			try
			{
				return this.ResolveType(position);
			}
			catch (Exception)
			{
				return null;
			}
		}

		ITypeDefOrRef ResolveType_NoLock(Int32 position)
		{
			this.Stream.Position = position;

			InlineOperand operand = new InlineOperand(this.Reader);
			if (operand.IsToken)
			{
				MDToken token = new MDToken(operand.Token);

				if (token.Table == Table.TypeDef)
					return this.Module.ResolveTypeDef(token.Rid);
				else if (token.Table == Table.TypeRef)
					return this.Module.ResolveTypeRef(token.Rid);
				else if (token.Table == Table.TypeSpec)
					return this.Module.ResolveTypeSpec(token.Rid);

				throw new Exception("[ResolveType_NoLock] Bad MDToken table");
			}
			else
			{
				TypeData data = operand.Data as TypeData;
				String typeName = data.TypeName;

				// Related to generic context
				if (data.SomeIndex != -1)
					this.Logger.Verbose(this, "[{0}] SomeIndex: {1}", data.TypeName, data.SomeIndex2);
				if (data.SomeIndex2 != -1)
					this.Logger.Verbose(this, "[{0}] SomeIndex2: {1}", data.TypeName, data.SomeIndex2);

				// Get the type modifiers stack
				Stack<String> modifiers = GetModifiersStack(data.TypeName, out typeName);

				// Try to find typedef
				TypeDef typeDef = this.Module.FindReflection(typeName);
				if (typeDef != null)
				{
					ITypeDefOrRef result = typeDef;
					if (data.GenericTypes.Length > 0)
						result = ApplyGenerics(typeDef, data);

					TypeSig sig = ApplyTypeModifiers(result.ToTypeSig(), modifiers);
					return sig.ToTypeDefOrRef();
				}

				// Otherwise, try to find typeref
				TypeRef typeRef = null;
				typeRef = this.Module.GetTypeRefs().FirstOrDefault((t) =>
				{
					return t.ReflectionFullName.Equals(typeName);
				});
				if (typeRef != null)
				{
					ITypeDefOrRef result = typeRef;
					if (data.GenericTypes.Length > 0)
						result = ApplyGenerics(typeRef, data);

					TypeSig sig = ApplyTypeModifiers(result.ToTypeSig(), modifiers);
					return sig.ToTypeDefOrRef();
				}

				// If all else fails, make our own typeref
				this.Logger.Verbose(this, "[ResolveType_NoLock] WARNING: Creating TypeRef for: {0}", data.Name);
				AssemblyRef assemblyRef = GetAssemblyRef(data.AssemblyFullName);
				typeRef = new TypeRefUser(this.Module, data.Namespace, data.TypeNameWithoutNamespace, assemblyRef);
				if (typeRef != null)
					return typeRef;

				throw new Exception(String.Format(
					"[ResolveType_NoLock] Couldn't resolve type {0} @ {1} (0x{1:X8})", typeName, position));
			}
		}

		/// <summary>
		/// Resolve a token.
		/// </summary>
		/// <param name="position">Position</param>
		/// <returns>Token</returns>
		public ITokenOperand ResolveToken(Int32 position)
		{
			lock (_lock)
			{
				return this.ResolveToken_NoLock(position);
			}
		}

		public ITokenOperand TryResolveToken(Int32 position)
		{
			try
			{
				return this.ResolveToken(position);
			}
			catch (Exception)
			{
				return null;
			}
		}

		ITokenOperand ResolveToken_NoLock(Int32 position)
		{
			this.Stream.Position = position;

			InlineOperand operand = new InlineOperand(this.Reader);
			if (operand.IsToken)
			{
				throw new NotSupportedException("Currently unable to resolve a token via MDToken");
			}
			else
			{
				if (operand.Data.Type == InlineOperandType.Type)
					return this.ResolveType_NoLock(operand.Position);
				else if (operand.Data.Type == InlineOperandType.Field)
					return this.ResolveField_NoLock(operand.Position);
				else if (operand.Data.Type == InlineOperandType.Method)
					return this.ResolveMethod_NoLock(operand.Position);
				else throw new InvalidOperationException(String.Format(
					"Expected inline operand type of token to be either Type, Field or Method; instead got {0}",
					operand.Data.Type));
			}
		}

		/// <summary>
		/// Resolve a type from a deserialized inline operand, which should
		/// have a direct token (position).
		/// </summary>
		/// <param name="operand">Inline operand</param>
		/// <returns>TypeSig</returns>
		TypeSig ResolveType(InlineOperand operand)
		{
			ITypeDefOrRef type = this.ResolveType_NoLock(operand.Position);
			return type.ToTypeSig(true);
		}

		/// <summary>
		/// Compare two method signatures, with the first having generic parameters
		/// of types from the declaring type's generic arguments.
		/// </summary>
		/// <param name="s1">First signature</param>
		/// <param name="s2">Second signature</param>
		/// <returns>true if appear equal, false if not</returns>
		/// <remarks>Make a comparer class for this later?</remarks>
		Boolean Equals(TypeSpec declaringType, MethodSig s1, MethodSig s2)
		{
			// This is necessary because the serialized MethodData doesn't contain
			// information (from what I can tell) about which parameters correspond
			// to which generic arguments.

			var comparer = new SigComparer();

			var gsig = declaringType.TryGetGenericInstSig();
			if (gsig == null)
			{
				// If declaring type isn't a generic instance, compare normally
				return comparer.Equals(s1, s2);
			}

			if (s1.Params.Count != s2.Params.Count)
				return false;

			for (Int32 i = 0; i < s1.Params.Count; i++)
			{
				var p = s1.Params[i];
				if (p.IsGenericTypeParameter)
				{
					var genericVar = p.ToGenericVar();
					var genericNumber = genericVar.GenericParam.Number;
					var genericArgument = gsig.GenericArguments[genericNumber];
					p = genericArgument;
				}

				if(!comparer.Equals(p, s2.Params[i]))
					return false;
			}

			return comparer.Equals(s1.RetType, s2.RetType);
		}

		ITypeDefOrRef ApplyGenerics(ITypeDefOrRef type, IList<TypeSig> generics)
		{
			ClassOrValueTypeSig typeSig = type.ToTypeSig().ToClassOrValueTypeSig();
			GenericInstSig genericSig = new GenericInstSig(typeSig, generics);
			return new TypeSpecUser(genericSig);
		}

		/// <summary>
		/// Create a TypeSpec from a type and data with generic arguments.
		/// from deserialized data to an existing type.
		/// </summary>
		/// <param name="type">Existing type</param>
		/// <param name="data">Deserialized data with generic types</param>
		/// <returns>GenericInstSig</returns>
		ITypeDefOrRef ApplyGenerics(ITypeDefOrRef type, TypeData data)
		{
			List<TypeSig> generics = new List<TypeSig>();
			foreach (var g in data.GenericTypes)
			{
				var gtype = this.ResolveType_NoLock(g.Position);
				generics.Add(gtype.ToTypeSig());
			}

			return ApplyGenerics(type, generics);

			//List<TypeSig> types = new List<TypeSig>();
			//foreach (var g in data.GenericTypes)
			//{
			//	var gtype = this.ResolveType_NoLock(g.Position);
			//	types.Add(gtype.ToTypeSig());
			//}
			//
			//ClassOrValueTypeSig typeSig = type.ToTypeSig().ToClassOrValueTypeSig();
			//// return new GenericInstSig(typeSig, types).ToTypeDefOrRef();
			//
			//GenericInstSig genericSig = new GenericInstSig(typeSig, types);
			//TypeSpec typeSpec = new TypeSpecUser(genericSig);
			//return typeSpec;
		}

		/// <summary>
		/// Create a MethodSpec from a MethodDef and data with generic arguments.
		/// If data contains no generic arguments, the passed method will be returned.
		/// </summary>
		/// <param name="method">Method</param>
		/// <param name="data">Data</param>
		/// <returns>MethodSpec or untouched Method if no generic arguments</returns>
		IMethod ToMethodSpec(IMethodDefOrRef method, MethodData data)
		{
			if (!data.HasGenericArguments)
				return method;

			// Resolve all generic argument types
			List<TypeSig> types = new List<TypeSig>();
			foreach (var g in data.GenericArguments)
			{
				var gtype = this.ResolveType_NoLock(g.Position);
				types.Add(gtype.ToTypeSig());
			}

			var sig = new GenericInstMethodSig(types);
			return new MethodSpecUser(method, sig);
		}

		/// <summary>
		/// Check whether or not a MethodDef matches some MethodSig.
		/// </summary>
		/// <param name="method">Method</param>
		/// <param name="signature">Method signature</param>
		/// <returns>true if matches, false if not</returns>
		/// <remarks>Could use dnlib's SigComparer for this maybe?</remarks>
		Boolean Matches(MethodDef method, MethodSig signature)
		{
			//This is imperfect and may confuse methods such as:
			// `MyMethod<T>(T, int): void` and `MyMethod<T>(int, T)`

			if (method.MethodSig.Params.Count != signature.Params.Count)
				return false;

			for (Int32 i = 0; i < method.MethodSig.Params.Count; i++)
			{
				TypeSig mp = method.MethodSig.Params[i];
				TypeSig sp = signature.Params[i];

				if (mp.IsGenericMethodParameter)
					continue;
				else if (!mp.MDToken.Equals(sp.MDToken))
					return false;
			}

			return method.MethodSig.RetType.MDToken.Equals(signature.RetType.MDToken)
				&& method.MethodSig.GenParamCount == signature.GenParamCount;
		}

		/// <summary>
		/// Create a list of all possible combinations of types/generic types that would make
		/// sense as parameters. This is necessary because the serialized method data does not
		/// contain information about which parameters map to which generic types (indices),
		/// neither GenericVars (declaring type) or GenericMVars (method itself).
		///
		/// TODO: Factor in context generics (generics from virtualized method itself and
		/// declaring type?)
		/// </summary>
		/// <param name="parameters">Parameters (with no generic type information)</param>
		/// <param name="generics">Generics visible to the method</param>
		/// <returns>Combinations with at least one item (original parameters)</returns>
		IList<IList<TypeSig>> CreateGenericParameterCombinations(IList<TypeSig> parameters,
			IList<TypeSig> typeGenerics, IList<TypeSig> methodGenerics)
		{
			IList<IList<TypeSig>> list = new List<IList<TypeSig>>();
			list.Add(parameters);

			for (UInt16 p = 0; p < parameters.Count; p++)
			{
				var ptype = parameters[p];

				for (UInt16 g = 0; g < typeGenerics.Count; g++)
				{
					var gtype = typeGenerics[g];

					// Better comparison?
					if (ptype.FullName.Equals(gtype.FullName))
					{
						Int32 length = list.Count;
						for (Int32 i = 0; i < length; i++)
						{
							// Copy param list
							List<TypeSig> newParams = new List<TypeSig>();
							newParams.AddRange(list[i]);

							GenericVar gvar = new GenericVar(g);
							newParams[p] = gvar;

							list.Add(newParams);
						}
					}
				}

				for (UInt16 g = 0; g < methodGenerics.Count; g++)
				{
					var gtype = methodGenerics[g];

					if (ptype.FullName.Equals(gtype.FullName))
					{
						Int32 length = list.Count;
						for (Int32 i = 0; i < length; i++)
						{
							List<TypeSig> newParams = new List<TypeSig>();
							newParams.AddRange(list[i]);

							GenericMVar gmvar = new GenericMVar(g);
							newParams[p] = gmvar;

							list.Add(newParams);
						}
					}
				}
			}

			return list;
		}

		IList<MethodSig> CreateMethodSigs(ITypeDefOrRef declaringType, MethodSig sig, MethodData data)
		{
			// Setup generic types
			IList<TypeSig> typeGenerics = new List<TypeSig>(), methodGenerics = new List<TypeSig>();

			// Add all declaring spec generic types
			TypeSpec declaringSpec = declaringType as TypeSpec;
			if (declaringSpec != null)
			{
				var genericInstSig = declaringSpec.TryGetGenericInstSig();
				foreach (var garg in genericInstSig.GenericArguments)
					typeGenerics.Add(garg);
			}

			// Add all method generic types
			if (data.HasGenericArguments)
			{
				foreach (var operand in data.GenericArguments)
				{
					var gtype = this.ResolveType_NoLock(operand.Position);
					methodGenerics.Add(gtype.ToTypeSig());
				}
			}

			if (!this.Logger.IgnoresEvent(LoggerEvent.Verbose))
			{
				this.Logger.Verbose(this, "Type Generics");
				foreach (var t in typeGenerics)
					this.Logger.Verbose(this, " {0}", t);

				this.Logger.Verbose(this, "Method Generics");
				foreach (var m in methodGenerics)
					this.Logger.Verbose(this, " {0}", m);
			}

			// Todo: Combinations factoring in the possibility that return type might match
			// a generic type
			TypeSig returnType = ResolveType(data.ReturnType);

			TypeSig[] paramTypes = new TypeSig[data.Parameters.Length];
			for (Int32 i = 0; i < paramTypes.Length; i++)
			{
				paramTypes[i] = ResolveType(data.Parameters[i]);
			}

			UInt32 genericTypeCount = (UInt32)data.GenericArguments.Length;

			IList<MethodSig> signatures = new List<MethodSig>();
			var paramCombos = CreateGenericParameterCombinations(paramTypes, typeGenerics, methodGenerics);

			foreach (var combo in paramCombos)
			{
				var paramCombo = combo.ToArray();

				MethodSig methodSig;

				if (genericTypeCount == 0)
				{
					if (data.IsStatic)
						methodSig = MethodSig.CreateStatic(returnType, paramCombo);
					else
						methodSig = MethodSig.CreateInstance(returnType, paramCombo);
				}
				else
				{
					if (data.IsStatic)
						methodSig = MethodSig.CreateStaticGeneric(genericTypeCount, returnType, paramCombo);
					else
						methodSig = MethodSig.CreateInstanceGeneric(genericTypeCount, returnType, paramCombo);
				}

				signatures.Add(methodSig);
			}

			return signatures;
		}

		/// <summary>
		/// Convert some method data into a method signature.
		/// </summary>
		/// <param name="data">Data</param>
		/// <returns>Signature</returns>
		MethodSig GetMethodSig(MethodData data)
		{
			TypeSig returnType = ResolveType(data.ReturnType);

			TypeSig[] paramTypes = new TypeSig[data.Parameters.Length];
			for (Int32 i = 0; i < paramTypes.Length; i++)
			{
				paramTypes[i] = ResolveType(data.Parameters[i]);
			}

			UInt32 genericTypeCount = (UInt32)data.GenericArguments.Length;

			MethodSig methodSig;
			if (genericTypeCount == 0)
			{
				if (data.IsStatic)
					methodSig = MethodSig.CreateStatic(returnType, paramTypes);
				else
					methodSig = MethodSig.CreateInstance(returnType, paramTypes);
			}
			else
			{
				if (data.IsStatic)
					methodSig = MethodSig.CreateStaticGeneric(genericTypeCount, returnType, paramTypes);
				else
					methodSig = MethodSig.CreateInstanceGeneric(genericTypeCount, returnType, paramTypes);
			}

			return methodSig;
		}

		/// <summary>
		/// Get a modifiers stack from a deserialized type name, and also
		/// provide the fixed name.
		/// </summary>
		/// <param name="rawName">Deserialized name</param>
		/// <param name="fixedName">Fixed name</param>
		/// <returns>Modifiers stack</returns>
		Stack<String> GetModifiersStack(String rawName, out String fixedName)
		{
			var stack = new Stack<String>();

			while(true)
			{
				if (rawName.EndsWith("*"))
					stack.Push("*");
				else if (rawName.EndsWith("&"))
					stack.Push("&");
				else break;

				rawName = rawName.Substring(0, rawName.Length - 1);
			}

			while(rawName.EndsWith("[]"))
			{
				stack.Push("[]");
				rawName = rawName.Substring(0, rawName.Length - 2);
			}

			fixedName = rawName;
			return stack;
		}

		/// <summary>
		/// Apply type "modifiers" to some TypeSig given a modifiers stack.
		/// </summary>
		/// <param name="typeSig">TypeSig</param>
		/// <param name="modifiers">Modifiers stack</param>
		/// <returns>TypeSig</returns>
		TypeSig ApplyTypeModifiers(TypeSig typeSig, Stack<String> modifiers)
		{
			// This might not be implemented correctly
			typeSig = this.FixTypeAsArray(typeSig, modifiers);
			typeSig = this.FixTypeAsRefOrPointer(typeSig, modifiers);
			return typeSig;
		}

		/// <summary>
		/// Apply array "modifiers" to a TypeSig.
		/// </summary>
		/// <param name="typeSig">TypeSig</param>
		/// <param name="modifiers">Modifiers stack</param>
		/// <returns>TypeSig</returns>
		TypeSig FixTypeAsArray(TypeSig typeSig, Stack<String> modifiers)
		{
			UInt32 rank = 0;

			while(modifiers.Count > 0 && modifiers.Peek().Equals("[]"))
			{
				modifiers.Pop();
				rank++;
			}

			if (rank == 0)
				return typeSig;
			else if (rank == 1)
				return new SZArraySig(typeSig);
			else // Might want to wrap in N SZArraySigs instead of ArraySig(type, N)?
				return new ArraySig(typeSig, rank);
		}

		/// <summary>
		/// Apply ByRef or Ptr "modifiers" to a TypeSig depending on the given modifiers
		/// stack.
		/// </summary>
		/// <param name="typeSig">TypeSig</param>
		/// <param name="modifiers">Modifiers stack</param>
		/// <returns>TypeSig</returns>
		TypeSig FixTypeAsRefOrPointer(TypeSig typeSig, Stack<String> modifiers)
		{
			while(modifiers.Count > 0
				&& (modifiers.Peek().Equals("*") || modifiers.Peek().Equals("&")))
			{
				if(modifiers.Pop().Equals("*")) // Pointer
				{
					typeSig = new PtrSig(typeSig);
				}
				else // ByRef
				{
					typeSig = new ByRefSig(typeSig);
				}
			}

			return typeSig;
		}

		AssemblyRef GetAssemblyRef(String fullname)
		{
			return this.Module.GetAssemblyRefs().FirstOrDefault((ar) =>
			{
				return ar.FullName.Equals(fullname);
			});
		}
	}
}