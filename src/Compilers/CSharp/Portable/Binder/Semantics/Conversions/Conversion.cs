﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.CodeAnalysis.CSharp.Symbols;
using Microsoft.CodeAnalysis.PooledObjects;
using Roslyn.Utilities;
using System;
using System.Collections.Immutable;
using System.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Microsoft.CodeAnalysis.CSharp
{
    /// <summary>
    /// Summarizes whether a conversion is allowed, and if so, which kind of conversion (and in some cases, the
    /// associated symbol).
    /// </summary>
    public struct Conversion : IEquatable<Conversion>, IConvertibleConversion
    {
        private readonly ConversionKind _kind;
        private readonly UncommonData? _uncommonData;

        // most conversions are trivial and do not require additional data besides Kind
        // in uncommon cases an instance of this class is attached to the conversion.
        private class UncommonData
        {
            public UncommonData(
                bool isExtensionMethod,
                bool isArrayIndex,
                UserDefinedConversionResult conversionResult,
                MethodSymbol? conversionMethod,
                ImmutableArray<Conversion> nestedConversions)
            {
                _conversionMethod = conversionMethod;
                _conversionResult = conversionResult;
                _nestedConversionsOpt = nestedConversions;

                _flags = isExtensionMethod ? IsExtensionMethodMask : (byte)0;
                if (isArrayIndex)
                {
                    _flags |= IsArrayIndexMask;
                }
            }

            internal readonly MethodSymbol? _conversionMethod;
            internal readonly ImmutableArray<Conversion> _nestedConversionsOpt;

            //no effect on Equals/GetHashCode
            internal readonly UserDefinedConversionResult _conversionResult;

            private const byte IsExtensionMethodMask = 1 << 0;
            private const byte IsArrayIndexMask = 1 << 1;
            private readonly byte _flags;

            internal bool IsExtensionMethod
            {
                get
                {
                    return (_flags & IsExtensionMethodMask) != 0;
                }
            }

            // used by dynamic, which needs to know if a conversion is an array index conversion.
            internal bool IsArrayIndex
            {
                get
                {
                    return (_flags & IsArrayIndexMask) != 0;
                }
            }
        }

        private class DeconstructionUncommonData : UncommonData
        {
            internal DeconstructionUncommonData(DeconstructMethodInfo deconstructMethodInfoOpt, ImmutableArray<Conversion> nestedConversions)
                : base(isExtensionMethod: false, isArrayIndex: false, conversionResult: default, conversionMethod: null, nestedConversions)
            {
                Debug.Assert(!nestedConversions.IsDefaultOrEmpty);
                DeconstructMethodInfo = deconstructMethodInfoOpt;
            }

            internal readonly DeconstructMethodInfo DeconstructMethodInfo;
        }

        private Conversion(
            ConversionKind kind,
            UncommonData? uncommonData)
        {
            _kind = kind;
            _uncommonData = uncommonData;
        }

        private Conversion(ConversionKind kind)
            : this(kind, null)
        {
        }

        internal Conversion(UserDefinedConversionResult conversionResult, bool isImplicit)
        {
            _kind = conversionResult.Kind == UserDefinedConversionResultKind.NoApplicableOperators
                ? ConversionKind.NoConversion
                : isImplicit ? ConversionKind.ImplicitUserDefined : ConversionKind.ExplicitUserDefined;

            _uncommonData = new UncommonData(
                isExtensionMethod: false,
                isArrayIndex: false,
                conversionResult: conversionResult,
                conversionMethod: null,
                nestedConversions: default);
        }

        // For the method group, lambda and anonymous method conversions
        internal Conversion(ConversionKind kind, MethodSymbol conversionMethod, bool isExtensionMethod)
        {
            this._kind = kind;
            _uncommonData = new UncommonData(
                isExtensionMethod: isExtensionMethod,
                isArrayIndex: false,
                conversionResult: default,
                conversionMethod: conversionMethod,
                nestedConversions: default);
        }

        internal Conversion(ConversionKind kind, ImmutableArray<Conversion> nestedConversions)
        {
            this._kind = kind;
            _uncommonData = new UncommonData(
                isExtensionMethod: false,
                isArrayIndex: false,
                conversionResult: default,
                conversionMethod: null,
                nestedConversions: nestedConversions);
        }

        internal Conversion(ConversionKind kind, DeconstructMethodInfo deconstructMethodInfo, ImmutableArray<Conversion> nestedConversions)
        {
            Debug.Assert(kind == ConversionKind.Deconstruction);

            this._kind = kind;
            _uncommonData = new DeconstructionUncommonData(deconstructMethodInfo, nestedConversions);
        }

        internal Conversion SetConversionMethod(MethodSymbol conversionMethod)
        {
            // we use this method to patch up the conversion method only in two cases - 
            // 1) when rewriting MethodGroup conversions and the method gets substituted.
            // 2) when lowering IntPtr conversion (a compat-related conversion which becomes a kind of a user-defined conversion)
            // in those cases it is ok to ignore existing _uncommonData.
            Debug.Assert(_kind == ConversionKind.MethodGroup || _kind == ConversionKind.IntPtr);

            return new Conversion(this.Kind, conversionMethod, isExtensionMethod: IsExtensionMethod);
        }

        internal Conversion SetArrayIndexConversionForDynamic()
        {
            Debug.Assert(_kind.IsDynamic());
            Debug.Assert(_uncommonData == null);

            return new Conversion(
                _kind,
                new UncommonData(
                    isExtensionMethod: false,
                    isArrayIndex: true,
                    conversionResult: default,
                    conversionMethod: null,
                    nestedConversions: default));
        }

        [Conditional("DEBUG")]
        private static void AssertTrivialConversion(ConversionKind kind)
        {
            bool isTrivial;

            switch (kind)
            {
                case ConversionKind.NoConversion:
                case ConversionKind.Identity:
                case ConversionKind.ImplicitConstant:
                case ConversionKind.ImplicitNumeric:
                case ConversionKind.ImplicitReference:
                case ConversionKind.ImplicitEnumeration:
                case ConversionKind.ImplicitThrow:
                case ConversionKind.AnonymousFunction:
                case ConversionKind.Boxing:
                case ConversionKind.NullLiteral:
                case ConversionKind.DefaultLiteral:
                case ConversionKind.ImplicitNullToPointer:
                case ConversionKind.ImplicitPointerToVoid:
                case ConversionKind.ExplicitPointerToPointer:
                case ConversionKind.ExplicitPointerToInteger:
                case ConversionKind.ExplicitIntegerToPointer:
                case ConversionKind.Unboxing:
                case ConversionKind.ExplicitReference:
                case ConversionKind.IntPtr:
                case ConversionKind.ExplicitEnumeration:
                case ConversionKind.ExplicitNumeric:
                case ConversionKind.ImplicitDynamic:
                case ConversionKind.ExplicitDynamic:
                case ConversionKind.InterpolatedString:
                case ConversionKind.InterpolatedStringHandler:
                    isTrivial = true;
                    break;

                default:
                    isTrivial = false;
                    break;
            }

            Debug.Assert(isTrivial, "this conversion needs additional data: " + kind);
        }

        internal static Conversion GetTrivialConversion(ConversionKind kind)
        {
            AssertTrivialConversion(kind);
            return new Conversion(kind);
        }

        internal static Conversion UnsetConversion => new Conversion(ConversionKind.UnsetConversionKind);
        internal static Conversion NoConversion => new Conversion(ConversionKind.NoConversion);
        internal static Conversion Identity => new Conversion(ConversionKind.Identity);
        internal static Conversion ImplicitConstant => new Conversion(ConversionKind.ImplicitConstant);
        internal static Conversion ImplicitNumeric => new Conversion(ConversionKind.ImplicitNumeric);
        internal static Conversion ImplicitReference => new Conversion(ConversionKind.ImplicitReference);
        internal static Conversion ImplicitEnumeration => new Conversion(ConversionKind.ImplicitEnumeration);
        internal static Conversion ImplicitThrow => new Conversion(ConversionKind.ImplicitThrow);
        internal static Conversion ObjectCreation => new Conversion(ConversionKind.ObjectCreation);
        internal static Conversion AnonymousFunction => new Conversion(ConversionKind.AnonymousFunction);
        internal static Conversion Boxing => new Conversion(ConversionKind.Boxing);
        internal static Conversion NullLiteral => new Conversion(ConversionKind.NullLiteral);
        internal static Conversion DefaultLiteral => new Conversion(ConversionKind.DefaultLiteral);
        internal static Conversion NullToPointer => new Conversion(ConversionKind.ImplicitNullToPointer);
        internal static Conversion PointerToVoid => new Conversion(ConversionKind.ImplicitPointerToVoid);
        internal static Conversion PointerToPointer => new Conversion(ConversionKind.ExplicitPointerToPointer);
        internal static Conversion PointerToInteger => new Conversion(ConversionKind.ExplicitPointerToInteger);
        internal static Conversion IntegerToPointer => new Conversion(ConversionKind.ExplicitIntegerToPointer);
        internal static Conversion Unboxing => new Conversion(ConversionKind.Unboxing);
        internal static Conversion ExplicitReference => new Conversion(ConversionKind.ExplicitReference);
        internal static Conversion IntPtr => new Conversion(ConversionKind.IntPtr);
        internal static Conversion ExplicitEnumeration => new Conversion(ConversionKind.ExplicitEnumeration);
        internal static Conversion ExplicitNumeric => new Conversion(ConversionKind.ExplicitNumeric);
        internal static Conversion ImplicitDynamic => new Conversion(ConversionKind.ImplicitDynamic);
        internal static Conversion ExplicitDynamic => new Conversion(ConversionKind.ExplicitDynamic);
        internal static Conversion InterpolatedString => new Conversion(ConversionKind.InterpolatedString);
        internal static Conversion InterpolatedStringHandler => new Conversion(ConversionKind.InterpolatedStringHandler);
        internal static Conversion Deconstruction => new Conversion(ConversionKind.Deconstruction);
        internal static Conversion PinnedObjectToPointer => new Conversion(ConversionKind.PinnedObjectToPointer);
        internal static Conversion ImplicitPointer => new Conversion(ConversionKind.ImplicitPointer);
        internal static Conversion FunctionType => new Conversion(ConversionKind.FunctionType);

        // trivial conversions that could be underlying in nullable conversion
        // NOTE: tuple conversions can be underlying as well, but they are not trivial 
        internal static ImmutableArray<Conversion> IdentityUnderlying => ConversionSingletons.IdentityUnderlying;
        internal static ImmutableArray<Conversion> ImplicitConstantUnderlying => ConversionSingletons.ImplicitConstantUnderlying;
        internal static ImmutableArray<Conversion> ImplicitNumericUnderlying => ConversionSingletons.ImplicitNumericUnderlying;
        internal static ImmutableArray<Conversion> ExplicitNumericUnderlying => ConversionSingletons.ExplicitNumericUnderlying;
        internal static ImmutableArray<Conversion> ExplicitEnumerationUnderlying => ConversionSingletons.ExplicitEnumerationUnderlying;
        internal static ImmutableArray<Conversion> PointerToIntegerUnderlying => ConversionSingletons.PointerToIntegerUnderlying;

        // these static fields are not directly inside the Conversion
        // because that causes CLR loader failure.
        private static class ConversionSingletons
        {
            internal static ImmutableArray<Conversion> IdentityUnderlying = ImmutableArray.Create(Identity);
            internal static ImmutableArray<Conversion> ImplicitConstantUnderlying = ImmutableArray.Create(ImplicitConstant);
            internal static ImmutableArray<Conversion> ImplicitNumericUnderlying = ImmutableArray.Create(ImplicitNumeric);
            internal static ImmutableArray<Conversion> ExplicitNumericUnderlying = ImmutableArray.Create(ExplicitNumeric);
            internal static ImmutableArray<Conversion> ExplicitEnumerationUnderlying = ImmutableArray.Create(ExplicitEnumeration);
            internal static ImmutableArray<Conversion> PointerToIntegerUnderlying = ImmutableArray.Create(PointerToInteger);
        }

        internal static Conversion MakeStackAllocToPointerType(Conversion underlyingConversion)
        {
            return new Conversion(ConversionKind.StackAllocToPointerType, ImmutableArray.Create(underlyingConversion));
        }

        internal static Conversion MakeStackAllocToSpanType(Conversion underlyingConversion)
        {
            return new Conversion(ConversionKind.StackAllocToSpanType, ImmutableArray.Create(underlyingConversion));
        }

        internal static Conversion MakeNullableConversion(ConversionKind kind, Conversion nestedConversion)
        {
            Debug.Assert(kind == ConversionKind.ImplicitNullable || kind == ConversionKind.ExplicitNullable);

            ImmutableArray<Conversion> nested;
            switch (nestedConversion.Kind)
            {
                case ConversionKind.Identity:
                    nested = IdentityUnderlying;
                    break;
                case ConversionKind.ImplicitConstant:
                    nested = ImplicitConstantUnderlying;
                    break;
                case ConversionKind.ImplicitNumeric:
                    nested = ImplicitNumericUnderlying;
                    break;
                case ConversionKind.ExplicitNumeric:
                    nested = ExplicitNumericUnderlying;
                    break;
                case ConversionKind.ExplicitEnumeration:
                    nested = ExplicitEnumerationUnderlying;
                    break;
                case ConversionKind.ExplicitPointerToInteger:
                    nested = PointerToIntegerUnderlying;
                    break;
                default:
                    nested = ImmutableArray.Create(nestedConversion);
                    break;
            }

            return new Conversion(kind, nested);
        }

        internal static Conversion MakeSwitchExpression(ImmutableArray<Conversion> innerConversions)
        {
            return new Conversion(ConversionKind.SwitchExpression, innerConversions);
        }

        internal static Conversion MakeConditionalExpression(ImmutableArray<Conversion> innerConversions)
        {
            return new Conversion(ConversionKind.ConditionalExpression, innerConversions);
        }

        internal ConversionKind Kind
        {
            get
            {
                return _kind;
            }
        }

        internal bool IsExtensionMethod
        {
            get
            {
                return _uncommonData?.IsExtensionMethod == true;
            }
        }

        internal bool IsArrayIndex
        {
            get
            {
                return _uncommonData?.IsArrayIndex == true;
            }
        }

        internal ImmutableArray<Conversion> UnderlyingConversions
        {
            get
            {
                return _uncommonData?._nestedConversionsOpt ?? default(ImmutableArray<Conversion>);
            }
        }

        internal MethodSymbol? Method
        {
            get
            {
                var uncommonData = _uncommonData;
                if (uncommonData != null)
                {
                    if (uncommonData._conversionMethod is object)
                    {
                        return uncommonData._conversionMethod;
                    }

                    var conversionResult = uncommonData._conversionResult;
                    if (conversionResult.Kind == UserDefinedConversionResultKind.Valid)
                    {
                        UserDefinedConversionAnalysis analysis = conversionResult.Results[conversionResult.Best];
                        return analysis.Operator;
                    }

                    if (uncommonData is DeconstructionUncommonData deconstruction
                        && deconstruction.DeconstructMethodInfo.Invocation is BoundCall call)
                    {
                        return call.Method;
                    }
                }

                return null;
            }
        }

        internal TypeParameterSymbol? ConstrainedToTypeOpt
        {
            get
            {
                var uncommonData = _uncommonData;
                if (uncommonData != null && uncommonData._conversionMethod is null)
                {
                    var conversionResult = uncommonData._conversionResult;
                    if (conversionResult.Kind == UserDefinedConversionResultKind.Valid)
                    {
                        UserDefinedConversionAnalysis analysis = conversionResult.Results[conversionResult.Best];
                        return analysis.ConstrainedToTypeOpt;
                    }
                }

                return null;
            }
        }

        internal DeconstructMethodInfo DeconstructionInfo
        {
            get
            {
                var uncommonData = (DeconstructionUncommonData?)_uncommonData;
                return uncommonData == null ? default : uncommonData.DeconstructMethodInfo;
            }
        }

        // CONSIDER: public?
        internal bool IsValid
        {
            get
            {
                if (!this.Exists)
                {
                    return false;
                }


                var nestedConversionsOpt = _uncommonData?._nestedConversionsOpt;
                if (nestedConversionsOpt != null)
                {
                    foreach (var conv in nestedConversionsOpt)
                    {
                        if (!conv.IsValid)
                        {
                            return false;
                        }
                    }

                    Debug.Assert(!this.IsUserDefined);
                    return true;
                }

                return !this.IsUserDefined ||
                    this.Method is object ||
                    _uncommonData?._conversionResult.Kind == UserDefinedConversionResultKind.Valid;
            }
        }

        /// <summary>
        /// Returns true if the conversion exists, either as an implicit or explicit conversion.
        /// </summary>
        /// <remarks>
        /// The existence of a conversion does not necessarily imply that the conversion is valid.
        /// For example, an ambiguous user-defined conversion may exist but may not be valid.
        /// </remarks>
        public bool Exists
        {
            get
            {
                return Kind != ConversionKind.NoConversion;
            }
        }

        /// <summary>
        /// Returns true if the conversion is implicit.
        /// </summary>
        /// <remarks>
        /// Implicit conversions are described in section 6.1 of the C# language specification.
        /// </remarks>
        public bool IsImplicit
        {
            get
            {
                return Kind.IsImplicitConversion();
            }
        }

        /// <summary>
        /// Returns true if the conversion is explicit.
        /// </summary>
        /// <remarks>
        /// Explicit conversions are described in section 6.2 of the C# language specification.
        /// </remarks>
        public bool IsExplicit
        {
            get
            {
                // All conversions are either implicit or explicit.
                return Exists && !IsImplicit;
            }
        }

        /// <summary>
        /// Returns true if the conversion is an identity conversion.
        /// </summary>
        /// <remarks>
        /// Identity conversions are described in section 6.1.1 of the C# language specification.
        /// </remarks>
        public bool IsIdentity
        {
            get
            {
                return Kind == ConversionKind.Identity;
            }
        }

        /// <summary>
        /// Returns true if the conversion is a stackalloc conversion.
        /// </summary>
        public bool IsStackAlloc
        {
            get
            {
                return Kind == ConversionKind.StackAllocToPointerType || Kind == ConversionKind.StackAllocToSpanType;
            }
        }

        /// <summary>
        /// Returns true if the conversion is an implicit numeric conversion or explicit numeric conversion. 
        /// </summary>
        /// <remarks>
        /// Implicit and explicit numeric conversions are described in sections 6.1.2 and 6.2.1 of the C# language specification.
        /// </remarks>
        public bool IsNumeric
        {
            get
            {
                return Kind == ConversionKind.ImplicitNumeric || Kind == ConversionKind.ExplicitNumeric;
            }
        }

        /// <summary>
        /// Returns true if the conversion is an implicit enumeration conversion or explicit enumeration conversion.
        /// </summary>
        /// <remarks>
        /// Implicit and explicit enumeration conversions are described in sections 6.1.3 and 6.2.2 of the C# language specification.
        /// </remarks>
        public bool IsEnumeration
        {
            get
            {
                return Kind == ConversionKind.ImplicitEnumeration || Kind == ConversionKind.ExplicitEnumeration;
            }
        }

        /// <summary>
        /// Returns true if the conversion is an implicit throw conversion.
        /// </summary>
        public bool IsThrow
        {
            get
            {
                return Kind == ConversionKind.ImplicitThrow;
            }
        }

        /// <summary>
        /// Returns true if the conversion is an implicit object creation expression conversion.
        /// </summary>
        internal bool IsObjectCreation
        {
            get
            {
                return Kind == ConversionKind.ObjectCreation;
            }
        }

        /// <summary>
        /// Returns true if the conversion is an implicit switch expression conversion.
        /// </summary>
        public bool IsSwitchExpression
        {
            get
            {
                return Kind == ConversionKind.SwitchExpression;
            }
        }

        /// <summary>
        /// Returns true if the conversion is an implicit conditional expression conversion.
        /// </summary>
        public bool IsConditionalExpression
        {
            get
            {
                return Kind == ConversionKind.ConditionalExpression;
            }
        }

        // TODO: update the language reference section number below.
        /// <summary>
        /// Returns true if the conversion is an interpolated string conversion.
        /// </summary>
        /// <remarks>
        /// The interpolated string conversion described in section 6.1.N of the C# language specification.
        /// </remarks>
        public bool IsInterpolatedString
        {
            get
            {
                return Kind == ConversionKind.InterpolatedString;
            }
        }

        /// <summary>
        /// Returns true if the conversion is an interpolated string builder conversion.
        /// </summary>
        public bool IsInterpolatedStringHandler
        {
            get
            {
                return Kind == ConversionKind.InterpolatedStringHandler;
            }
        }

        /// <summary>
        /// Returns true if the conversion is an implicit nullable conversion or explicit nullable conversion.
        /// </summary>
        /// <remarks>
        /// Implicit and explicit nullable conversions are described in sections 6.1.4 and 6.2.3 of the C# language specification.
        /// </remarks>
        public bool IsNullable
        {
            get
            {
                return Kind == ConversionKind.ImplicitNullable || Kind == ConversionKind.ExplicitNullable;
            }
        }

        /// <summary>
        /// Returns true if the conversion is an implicit tuple literal conversion or explicit tuple literal conversion.
        /// </summary>
        public bool IsTupleLiteralConversion
        {
            get
            {
                return Kind == ConversionKind.ImplicitTupleLiteral || Kind == ConversionKind.ExplicitTupleLiteral;
            }
        }

        /// <summary>
        /// Returns true if the conversion is an implicit tuple conversion or explicit tuple conversion.
        /// </summary>
        public bool IsTupleConversion
        {
            get
            {
                return Kind == ConversionKind.ImplicitTuple || Kind == ConversionKind.ExplicitTuple;
            }
        }

        /// <summary>
        /// Returns true if the conversion is an implicit reference conversion or explicit reference conversion.
        /// </summary>
        /// <remarks>
        /// Implicit and explicit reference conversions are described in sections 6.1.6 and 6.2.4 of the C# language specification.
        /// </remarks>
        public bool IsReference
        {
            get
            {
                return Kind == ConversionKind.ImplicitReference || Kind == ConversionKind.ExplicitReference;
            }
        }

        /// <summary>
        /// Returns true if the conversion is an implicit user-defined conversion or explicit user-defined conversion.
        /// </summary>
        /// <remarks>
        /// Implicit and explicit user-defined conversions are described in section 6.4 of the C# language specification.
        /// </remarks>
        public bool IsUserDefined
        {
            get
            {
                return Kind.IsUserDefinedConversion();
            }
        }

        /// <summary>
        /// Returns true if the conversion is an implicit boxing conversion.
        /// </summary>
        /// <remarks>
        /// Implicit boxing conversions are described in section 6.1.7 of the C# language specification.
        /// </remarks>
        public bool IsBoxing
        {
            get
            {
                return Kind == ConversionKind.Boxing;
            }
        }

        /// <summary>
        /// Returns true if the conversion is an explicit unboxing conversion.
        /// </summary>
        /// <remarks>
        /// Explicit unboxing conversions as described in section 6.2.5 of the C# language specification.
        /// </remarks>
        public bool IsUnboxing
        {
            get
            {
                return Kind == ConversionKind.Unboxing;
            }
        }

        /// <summary>
        /// Returns true if the conversion is an implicit null literal conversion.
        /// </summary>
        /// <remarks>
        /// Null literal conversions are described in section 6.1.5 of the C# language specification.
        /// </remarks>
        public bool IsNullLiteral
        {
            get
            {
                return Kind == ConversionKind.NullLiteral;
            }
        }

        /// <summary>
        /// Returns true if the conversion is an implicit default literal conversion.
        /// </summary>
        public bool IsDefaultLiteral
        {
            get
            {
                return Kind == ConversionKind.DefaultLiteral;
            }
        }

        /// <summary>
        /// Returns true if the conversion is an implicit dynamic conversion. 
        /// </summary>
        /// <remarks>
        /// Implicit dynamic conversions are described in section 6.1.8 of the C# language specification.
        /// </remarks>
        public bool IsDynamic
        {
            get
            {
                return Kind.IsDynamic();
            }
        }

        /// <summary>
        /// Returns true if the conversion is an implicit constant expression conversion.
        /// </summary>
        /// <remarks>
        /// Implicit constant expression conversions are described in section 6.1.9 of the C# language specification.
        /// </remarks>
        public bool IsConstantExpression
        {
            get
            {
                return Kind == ConversionKind.ImplicitConstant;
            }
        }

        /// <summary>
        /// Returns true if the conversion is an implicit anonymous function conversion.
        /// </summary>
        /// <remarks>
        /// Implicit anonymous function conversions are described in section 6.5 of the C# language specification.
        /// </remarks>
        public bool IsAnonymousFunction
        {
            get
            {
                return Kind == ConversionKind.AnonymousFunction;
            }
        }

        /// <summary>
        /// Returns true if the conversion is an implicit method group conversion.
        /// </summary>
        /// <remarks>
        /// Implicit method group conversions are described in section 6.6 of the C# language specification.
        /// </remarks>
        public bool IsMethodGroup
        {
            get
            {
                return Kind == ConversionKind.MethodGroup;
            }
        }

        /// <summary>
        /// Returns true if the conversion is a pointer conversion 
        /// </summary>
        /// <remarks>
        /// Pointer conversions are described in section 18.4 of the C# language specification.
        /// 
        /// Returns true if the conversion is a conversion 
        ///  a) from a pointer type to void*, 
        ///  b) from a pointer type to another pointer type (other than void*),
        ///  c) from the null literal to a pointer type,
        ///  d) from an integral numeric type to a pointer type,
        ///  e) from a pointer type to an integral numeric type, or
        ///  d) from a function pointer type to a function pointer type.
        /// 
        /// Does not return true for user-defined conversions to/from pointer types.
        /// Does not return true for conversions between pointer types and IntPtr/UIntPtr.
        /// </remarks>
        public bool IsPointer
        {
            get
            {
                return this.Kind.IsPointerConversion();
            }
        }

        /// <summary>
        /// Returns true if the conversion is a conversion to or from IntPtr or UIntPtr.
        /// </summary>
        /// <remarks>
        /// Returns true if the conversion is a conversion to or from IntPtr or UIntPtr.
        /// This includes:
        ///   IntPtr to/from int
        ///   IntPtr to/from long
        ///   IntPtr to/from void*
        ///   UIntPtr to/from int
        ///   UIntPtr to/from long
        ///   UIntPtr to/from void*
        /// </remarks>
        public bool IsIntPtr
        {
            get
            {
                return Kind == ConversionKind.IntPtr;
            }
        }

        /// <summary>
        /// Returns the method used to create the delegate for a method group conversion if <see cref="IsMethodGroup"/> is true 
        /// or the method used to perform the conversion for a user-defined conversion if <see cref="IsUserDefined"/> is true.
        /// Otherwise, returns null.
        /// </summary>
        /// <remarks>
        /// Method group conversions are described in section 6.6 of the C# language specification.
        /// User-defined conversions are described in section 6.4 of the C# language specification.
        /// </remarks>
        public IMethodSymbol? MethodSymbol
        {
            get
            {
                return this.Method.GetPublicSymbol();
            }
        }

        /// <summary>
        /// Gives an indication of how successful the conversion was.
        /// Viable - found a best built-in or user-defined conversion.
        /// Empty - found no applicable built-in or user-defined conversions.
        /// OverloadResolutionFailure - found applicable conversions, but no unique best.
        /// </summary>
        internal LookupResultKind ResultKind
        {
            get
            {
                var conversionResult = _uncommonData?._conversionResult ?? default(UserDefinedConversionResult);

                switch (conversionResult.Kind)
                {
                    case UserDefinedConversionResultKind.Valid:
                        return LookupResultKind.Viable;
                    case UserDefinedConversionResultKind.Ambiguous:
                    case UserDefinedConversionResultKind.NoBestSourceType:
                    case UserDefinedConversionResultKind.NoBestTargetType:
                        return LookupResultKind.OverloadResolutionFailure;
                    case UserDefinedConversionResultKind.NoApplicableOperators:
                        if (conversionResult.Results.IsDefaultOrEmpty)
                        {
                            return this.Kind == ConversionKind.NoConversion ? LookupResultKind.Empty : LookupResultKind.Viable;
                        }
                        else
                        {
                            // CONSIDER: indicating an overload resolution failure is sufficient,
                            // but it would be nice to indicate lack of accessibility or other
                            // error conditions.
                            return LookupResultKind.OverloadResolutionFailure;
                        }
                    default:
                        throw ExceptionUtilities.UnexpectedValue(conversionResult.Kind);
                }
            }
        }

        /// <summary>
        /// Conversion applied to operand of the user-defined conversion.
        /// </summary>
        internal Conversion UserDefinedFromConversion
        {
            get
            {
                UserDefinedConversionAnalysis? best = BestUserDefinedConversionAnalysis;
                return best == null ? Conversion.NoConversion : best.SourceConversion;
            }
        }

        /// <summary>
        /// Conversion applied to the result of the user-defined conversion.
        /// </summary>
        internal Conversion UserDefinedToConversion
        {
            get
            {
                UserDefinedConversionAnalysis? best = BestUserDefinedConversionAnalysis;
                return best == null ? Conversion.NoConversion : best.TargetConversion;
            }
        }

        /// <summary>
        /// The user-defined operators that were considered when attempting this conversion
        /// (i.e. the arguments to overload resolution).
        /// </summary>
        internal ImmutableArray<MethodSymbol> OriginalUserDefinedConversions
        {
            get
            {
                // If overload resolution has failed then we want to stash away the original methods that we 
                // considered so that the IDE can display tooltips or other information about them.
                // However, if a method group contained a generic method that was type inferred then
                // the IDE wants information about the *inferred* method, not the original unconstructed
                // generic method.

                if (_uncommonData == null)
                {
                    return ImmutableArray<MethodSymbol>.Empty;
                }

                var conversionResult = _uncommonData._conversionResult;
                if (conversionResult.Kind == UserDefinedConversionResultKind.NoApplicableOperators)
                {
                    return ImmutableArray<MethodSymbol>.Empty;
                }

                var builder = ArrayBuilder<MethodSymbol>.GetInstance();
                foreach (var analysis in conversionResult.Results)
                {
                    builder.Add(analysis.Operator);
                }
                return builder.ToImmutableAndFree();
            }
        }

        internal UserDefinedConversionAnalysis? BestUserDefinedConversionAnalysis
        {
            get
            {
                if (_uncommonData == null)
                {
                    return null;
                }

                var conversionResult = _uncommonData._conversionResult;

                if (conversionResult.Kind == UserDefinedConversionResultKind.Valid)
                {
                    UserDefinedConversionAnalysis analysis = conversionResult.Results[conversionResult.Best];
                    return analysis;
                }

                return null;
            }
        }

        /// <summary>
        /// Creates a <seealso cref="CommonConversion"/> from this C# conversion.
        /// </summary>
        /// <returns>The <see cref="CommonConversion"/> that represents this conversion.</returns>
        /// <remarks>
        /// This is a lossy conversion; it is not possible to recover the original <see cref="Conversion"/>
        /// from the <see cref="CommonConversion"/> struct.
        /// </remarks>
        public CommonConversion ToCommonConversion()
        {
            // The MethodSymbol of CommonConversion only refers to UserDefined conversions, not method groups
            var methodSymbol = IsUserDefined ? MethodSymbol : null;
            return new CommonConversion(Exists, IsIdentity, IsNumeric, IsReference, IsImplicit, IsNullable, methodSymbol);
        }

        /// <summary>
        /// Returns a string that represents the <see cref="Kind"/> of the conversion.
        /// </summary>
        /// <returns>A string that represents the <see cref="Kind"/> of the conversion.</returns>
        public override string ToString()
        {
            return this.Kind.ToString();
        }

        /// <summary>
        /// Determines whether the specified <see cref="Conversion"/> object is equal to the current <see cref="Conversion"/> object.
        /// </summary>
        /// <param name="obj">The <see cref="Conversion"/> object to compare with the current <see cref="Conversion"/> object.</param>
        /// <returns>true if the specified <see cref="Conversion"/> object is equal to the current <see cref="Conversion"/> object; otherwise, false.</returns>
        public override bool Equals(object? obj)
        {
            return obj is Conversion && this.Equals((Conversion)obj);
        }

        /// <summary>
        /// Determines whether the specified <see cref="Conversion"/> object is equal to the current <see cref="Conversion"/> object.
        /// </summary>
        /// <param name="other">The <see cref="Conversion"/> object to compare with the current <see cref="Conversion"/> object.</param>
        /// <returns>true if the specified <see cref="Conversion"/> object is equal to the current <see cref="Conversion"/> object; otherwise, false.</returns>
        public bool Equals(Conversion other)
        {
            return this.Kind == other.Kind && this.Method == other.Method;
        }

        /// <summary>
        /// Returns a hash code for the current <see cref="Conversion"/> object.
        /// </summary>
        /// <returns>A hash code for the current <see cref="Conversion"/> object.</returns>
        public override int GetHashCode()
        {
            return Hash.Combine(this.Method, (int)this.Kind);
        }

        /// <summary>
        /// Returns true if the specified <see cref="Conversion"/> objects are equal and false otherwise.
        /// </summary>
        /// <param name="left">The first <see cref="Conversion"/> object.</param>
        /// <param name="right">The second <see cref="Conversion"/> object.</param>
        /// <returns></returns>
        public static bool operator ==(Conversion left, Conversion right)
        {
            return left.Equals(right);
        }

        /// <summary>
        /// Returns false if the specified <see cref="Conversion"/> objects are equal and true otherwise.
        /// </summary>
        /// <param name="left">The first <see cref="Conversion"/> object.</param>
        /// <param name="right">The second <see cref="Conversion"/> object.</param>
        /// <returns></returns>
        public static bool operator !=(Conversion left, Conversion right)
        {
            return !(left == right);
        }

#if DEBUG
        internal string Dump()
        {
            return TreeDumper.DumpCompact(Dump(this));

            TreeDumperNode Dump(Conversion self)
            {
                var sub = new System.Collections.Generic.List<TreeDumperNode>();

                if (self.Method is object)
                {
                    sub.Add(new TreeDumperNode("method", self.Method.ToDisplayString(), null));
                }

                if (!self.DeconstructionInfo.IsDefault)
                {
                    sub.Add(new TreeDumperNode("deconstructionInfo", null,
                        new[] { BoundTreeDumperNodeProducer.MakeTree(self.DeconstructionInfo.Invocation) }));
                }

                var underlyingConversions = self.UnderlyingConversions;
                if (!underlyingConversions.IsDefaultOrEmpty)
                {
                    sub.Add(new TreeDumperNode($"underlyingConversions[{underlyingConversions.Length}]", null,
                        underlyingConversions.SelectAsArray(c => Dump(c))));
                }

                return new TreeDumperNode("conversion", self.Kind, sub);
            }
        }
#endif
    }

    /// <summary>Stores all the information from binding for calling a Deconstruct method.</summary>
    internal struct DeconstructMethodInfo
    {
        internal DeconstructMethodInfo(BoundExpression invocation, BoundDeconstructValuePlaceholder inputPlaceholder,
            ImmutableArray<BoundDeconstructValuePlaceholder> outputPlaceholders)
        {
            (Invocation, InputPlaceholder, OutputPlaceholders) = (invocation, inputPlaceholder, outputPlaceholders);
        }

        internal readonly BoundExpression Invocation;
        internal readonly BoundDeconstructValuePlaceholder InputPlaceholder;
        internal readonly ImmutableArray<BoundDeconstructValuePlaceholder> OutputPlaceholders;
        internal bool IsDefault => Invocation is null;
    }
}
