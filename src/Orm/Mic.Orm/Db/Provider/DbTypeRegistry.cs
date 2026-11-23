using System.Globalization;
using Mic.Orm.Db.Model;
using Mic.Orm.Model;

namespace Mic.Orm.Db.Provider;

/// <summary>Represents a database type as the vendor defines it: its name, the CLR type a reader returns for it, its
/// aliases and the arguments it takes.</summary>
public class DbTypeDef {
  public DbTypeDef(string name, Type clrType, DbTypeFlags flags = DbTypeFlags.None,
      string? aliases = null, DbSpecialType specialType = DbSpecialType.None,
      byte defaultPrecision = 0, byte defaultScale = 0, int defaultSize = 0) {
    Name = name;
    DefaultSize = defaultSize;
    ClrType = clrType;
    Flags = flags;
    SpecialType = specialType;
    DefaultPrecision = defaultPrecision;
    DefaultScale = defaultScale;
    Aliases = aliases?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries) ?? [];
  }

  public string Name { get; }

  /// <summary>Gets the CLR type a reader returns for this database type.</summary>
  public Type ClrType { get; }

  public DbTypeFlags Flags { get; }
  public DbSpecialType SpecialType { get; }
  public byte DefaultPrecision { get; }
  public byte DefaultScale { get; }

  /// <summary>Gets the length used when a member asks for none, for a type that needs one, such as the
  /// <c>char(36)</c> a server without a Guid type stores a Guid as.</summary>
  public int DefaultSize { get; }

  /// <summary>Gets the other names the type answers to, such as the names a server's catalog reports.</summary>
  public IReadOnlyList<string> Aliases { get; }

  /// <summary>Gets or sets the placeholder literal an upgrade fills a new required column's existing rows with, such
  /// as <c>''</c>, <c>0</c> or a zero Guid, or <see langword="null"/> where the type has none.</summary>
  /// <remarks>A migration attached to the add-columns stage runs before the placeholder is applied, so it gets first
  /// refusal on every row.</remarks>
  public string? ColumnInit { get; set; }

  public bool TakesSize => Flags.HasFlag(DbTypeFlags.TakesSize);
  public bool TakesPrecisionScale => Flags.HasFlag(DbTypeFlags.TakesPrecisionScale);
  public bool IsUnlimited => Flags.HasFlag(DbTypeFlags.Unlimited);
  public bool TakesArguments => TakesSize || TakesPrecisionScale;

  /// <summary>Gets a value that indicates whether the type is one its vendor has superseded, resolvable by name but
  /// never chosen by mapping.</summary>
  public bool IsLegacy => Flags.HasFlag(DbTypeFlags.Legacy);

  public override string ToString() => Name;
}

/// <summary>Represents a database type with its arguments filled in, such as <c>varchar(50)</c>.</summary>
public class DbTypeInfo {
  public DbTypeInfo(DbTypeDef typeDef, string typeSpec, int size = 0, byte precision = 0, byte scale = 0) {
    TypeDef = typeDef;
    TypeSpec = typeSpec;
    Size = size;
    Precision = precision;
    Scale = scale;
  }

  public DbTypeDef TypeDef { get; }

  /// <summary>Gets the type exactly as it is written, such as <c>varchar(50)</c>.</summary>
  public string TypeSpec { get; }

  public int Size { get; }
  public byte Precision { get; }
  public byte Scale { get; }

  public Type ClrType => TypeDef.ClrType;

  public DbTypeRef ToTypeRef() => new(TypeSpec, ClrType);

  public override string ToString() => TypeSpec;
}

/// <summary>Holds the database types a provider's server has, mapping a CLR type forward to its preferred database
/// type and any database type name, alias included, back to its definition.</summary>
public class DbTypeRegistry {
  readonly Dictionary<string, DbTypeDef> _byName = new(StringComparer.OrdinalIgnoreCase);
  readonly Dictionary<Type, DbTypeDef> _preferredByClrType = new();
  readonly Dictionary<DbSpecialType, DbTypeDef> _bySpecialType = new();
  readonly List<DbTypeDef> _defs = new();

  public IReadOnlyList<DbTypeDef> Types => _defs;

  /// <summary>Registers a type.</summary>
  /// <param name="name">The name of the type.</param>
  /// <param name="clrType">The CLR type a reader returns for it.</param>
  /// <param name="flags">A bitwise combination of the enumeration values that specifies what arguments the type
  /// takes.</param>
  /// <param name="aliases">The other names the type answers to, comma-separated, or <see langword="null"/>.</param>
  /// <param name="specialType">One of the enumeration values that specifies the type's semantic category.</param>
  /// <param name="defaultPrecision">The precision used when a member names none.</param>
  /// <param name="defaultScale">The scale used when a member names none.</param>
  /// <param name="columnInit">The placeholder literal for the type, or <see langword="null"/> for the default of
  /// its CLR type.</param>
  /// <param name="defaultSize">The length used when a member names none.</param>
  /// <returns>The registered type.</returns>
  /// <remarks>The first non-legacy, bounded registration for a CLR type becomes the preferred one for that type.
  /// </remarks>
  public DbTypeDef Add(string name, Type clrType, DbTypeFlags flags = DbTypeFlags.None,
      string? aliases = null, DbSpecialType specialType = DbSpecialType.None,
      byte defaultPrecision = 0, byte defaultScale = 0, string? columnInit = null, int defaultSize = 0) {
    var def = new DbTypeDef(name, clrType, flags, aliases, specialType, defaultPrecision, defaultScale, defaultSize) {
      ColumnInit = columnInit ?? GetDefaultColumnInit(clrType),
    };
    if (def.IsLegacy && specialType != DbSpecialType.None)
      throw new ArgumentException(
        $"Type '{name}' is marked Legacy and given the special type {specialType} — a legacy type must never be what "
        + "the base reaches for. Register the modern type for that category instead.");

    _defs.Add(def);

    _byName[name] = def;
    foreach (var alias in def.Aliases)
      _byName[alias] = def;

    // A legacy type is resolvable by name but never chosen by mapping; nor is a vector type, which a member takes
    // by its mark and not by being an array of floats.
    if (!def.IsLegacy && !def.IsUnlimited && specialType != DbSpecialType.Vector
        && !_preferredByClrType.ContainsKey(clrType))
      _preferredByClrType[clrType] = def;
    if (specialType != DbSpecialType.None)
      _bySpecialType[specialType] = def;
    return def;
  }

  /// <summary>Makes a registered type the preferred one for a CLR type the server has no type of its own for, such
  /// as a geographic point held as bytes.</summary>
  /// <param name="clrType">The CLR type.</param>
  /// <param name="typeName">The name of the registered type.</param>
  /// <exception cref="ArgumentException">No type of the name is registered.</exception>
  public void Prefer(Type clrType, string typeName) {
    _preferredByClrType[clrType] = FindByName(typeName)
      ?? throw new ArgumentException($"Type '{typeName}' is not registered.", nameof(typeName));
  }

  /// <summary>Gets the placeholder literal for a column of a CLR type.</summary>
  /// <param name="clrType">The CLR type.</param>
  /// <returns>The literal, or <see langword="null"/> if the type has no placeholder, as a binary type does not.
  /// </returns>
  /// <remarks>A provider overrides this where its server spells a literal differently, such as a Boolean as
  /// <c>false</c> rather than <c>0</c>.</remarks>
  public virtual string? GetDefaultColumnInit(Type clrType) {
    var type = Nullable.GetUnderlyingType(clrType) ?? clrType;
    if (type == typeof(string))
      return "''";
    if (type.IsEnum)
      return "0";
    if (type == typeof(bool))
      return "0";
    if (type == typeof(Guid))
      return $"'{Guid.Empty}'";
    if (type == typeof(DateTime) || type == typeof(DateTimeOffset))
      return "'1900-01-01'";
    if (type == typeof(DateOnly))
      return "'1900-01-01'";
    if (type == typeof(TimeOnly) || type == typeof(TimeSpan))
      return "'00:00:00'";
    if (type == typeof(byte) || type == typeof(short) || type == typeof(int) || type == typeof(long)
        || type == typeof(float) || type == typeof(double) || type == typeof(decimal))
      return "0";
    return null;
  }

  /// <summary>Finds a type by its name or one of its aliases, ignoring case.</summary>
  /// <param name="name">The name as a server or a type spec reports it.</param>
  /// <returns>The type, or <see langword="null"/> if none is registered under the name.</returns>
  public DbTypeDef? FindByName(string name) =>
    _byName.TryGetValue(name.Trim(), out var def) ? def : null;

  public DbTypeDef? FindSpecial(DbSpecialType specialType) =>
    _bySpecialType.TryGetValue(specialType, out var def) ? def : null;

  /// <summary>Maps a CLR type to its database type, with a size or a precision and scale where the type takes them.
  /// </summary>
  /// <param name="clrType">The CLR type.</param>
  /// <param name="size">The length, or 0 for the type's default.</param>
  /// <param name="precision">The precision, or 0 for the type's default.</param>
  /// <param name="scale">The scale, or 0 for the type's default.</param>
  /// <param name="unlimited"><see langword="true"/> to prefer the unbounded form of a string or binary type;
  /// otherwise, <see langword="false"/>.</param>
  /// <returns>The database type, or <see langword="null"/> if none is registered for the CLR type.</returns>
  public DbTypeInfo? MapType(Type clrType, int size = 0, byte precision = 0, byte scale = 0, bool unlimited = false) {
    ArgumentNullException.ThrowIfNull(clrType);
    var def = FindPreferred(Nullable.GetUnderlyingType(clrType) ?? clrType, unlimited, size);
    return def == null ? null : Apply(def, size, precision, scale);
  }

  /// <summary>Maps a CLR type to its database type with no size stated, as a function's parameter is typed: the
  /// unlimited form for a string or a binary.</summary>
  /// <param name="clrType">The CLR type, nullable or not.</param>
  /// <returns>The database type, or <see langword="null"/> if none is registered for the CLR type.</returns>
  public DbTypeInfo? MapType(Type clrType) {
    ArgumentNullException.ThrowIfNull(clrType);
    var type = Nullable.GetUnderlyingType(clrType) ?? clrType;
    if (type.IsEnum)
      type = Enum.GetUnderlyingType(type);
    var def = FindPreferred(type, unlimited: type == typeof(string) || type == typeof(byte[]));
    return def == null ? null : Apply(def);
  }

  /// <summary>Maps an entity member to its database type, parsing and validating the member's own type spec where it
  /// names one.</summary>
  /// <param name="member">The entity member.</param>
  /// <param name="error">When this method returns, contains why the member cannot be mapped, or
  /// <see langword="null"/> if it can.</param>
  /// <returns>The database type, or <see langword="null"/> if the member cannot be mapped.</returns>
  public virtual DbTypeInfo? MapMember(EntityMemberInfo member, out string? error) {
    error = null;
    if (member.DbTypeSpec != null)
      return ParseSpec(member.DbTypeSpec, out error);

    // A JSON member takes the server's JSON type whatever its CLR type, or unlimited text where the server has none.
    if (member.IsJson) {
      var jsonDef = FindSpecial(DbSpecialType.Json) ?? FindSpecial(DbSpecialType.StringUnlimited);
      if (jsonDef == null) {
        error = "no JSON type and no unlimited text type is registered to hold a JSON member.";
        return null;
      }
      return Apply(jsonDef);
    }

    // A vector member takes the server's vector type, with its dimensions as the size.
    if (member.IsVector) {
      var vectorDef = FindSpecial(DbSpecialType.Vector);
      if (vectorDef == null) {
        error = "no vector type is registered to hold a [Vector] member: this server's provider has none, or has "
          + "one for a later version of the server than the provider was created for.";
        return null;
      }
      return Apply(vectorDef, member.Size);
    }

    var clrType = GetUnderlyingType(member);
    var unlimited = member.MemberFlags.HasFlag(MemberFlags.Unlimited);

    var def = FindPreferred(clrType, unlimited, member.Size);
    if (def == null) {
      error = member.ArrayItemType != null
        ? $"no array type is registered for '{clrType.Name}': an array of scalars is a column on a server that has "
          + "array types, PostgreSQL; elsewhere the member wants [Json]."
        : $"no database type is registered for '{clrType.Name}'.";
      return null;
    }
    return Apply(def, member.Size, member.Precision, member.Scale);
  }

  /// <summary>Parses and validates a type spec such as <c>varchar(50)</c>, <c>numeric(18,4)</c> or <c>geography</c>.
  /// </summary>
  /// <param name="spec">The type spec.</param>
  /// <param name="error">When this method returns, contains why the spec is not valid, or <see langword="null"/>
  /// if it is.</param>
  /// <returns>The database type, with the spec kept as written, or <see langword="null"/> if the spec is malformed
  /// or names a type the provider does not know.</returns>
  public DbTypeInfo? ParseSpec(string spec, out string? error) {
    error = null;
    var text = spec.Trim();
    var name = text;
    var args = Array.Empty<string>();

    var open = text.IndexOf('(');
    if (open > 0) {
      if (!text.EndsWith(')')) {
        error = $"database type '{spec}' is malformed.";
        return null;
      }
      name = text[..open].Trim();
      args = text[(open + 1)..^1]
        .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    }

    var def = FindByName(name);
    if (def == null) {
      error = $"database type '{name}' is not known to this provider.";
      return null;
    }

    int size = 0;
    byte precision = 0, scale = 0;
    if (args.Length == 1 && int.TryParse(args[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var argument)) {
      if (def.TakesPrecisionScale)
        precision = (byte)argument;
      else
        size = argument;
    } else if (args.Length == 2) {
      byte.TryParse(args[0], out precision);
      byte.TryParse(args[1], out scale);
    }

    // The spec is kept as written, not reformatted.
    return new DbTypeInfo(def, text, size, precision, scale);
  }

  /// <summary>Applies arguments to a type, formatting its spec.</summary>
  /// <param name="def">The type.</param>
  /// <param name="size">The length, or 0 for the type's default.</param>
  /// <param name="precision">The precision, or 0 for the type's default.</param>
  /// <param name="scale">The scale, or 0 for the type's default.</param>
  /// <returns>The type with its arguments filled in.</returns>
  public DbTypeInfo Apply(DbTypeDef def, int size = 0, byte precision = 0, byte scale = 0) {
    if (def.TakesSize) {
      var length = size > 0 ? size : def.DefaultSize;
      if (length > 0)
        return new DbTypeInfo(def, $"{def.Name}({length})", length);
    }
    if (def.TakesPrecisionScale) {
      var usedPrecision = precision != 0 ? precision : def.DefaultPrecision;
      var usedScale = scale != 0 ? scale : def.DefaultScale;
      if (usedPrecision != 0)
        return new DbTypeInfo(def, $"{def.Name}({usedPrecision},{usedScale})", 0, usedPrecision, usedScale);
    }
    return new DbTypeInfo(def, def.Name);
  }

  // A vendor need not have both a bounded and an unbounded form, so one falls back to the other. A binary that says
  // no size is unlimited: the bounded binary types take no statement without one, or mean one byte.
  private DbTypeDef? FindPreferred(Type clrType, bool unlimited, int size = 0) {
    if (clrType == typeof(string))
      return FindSpecialType(DbSpecialType.String, DbSpecialType.StringUnlimited, unlimited)
        ?? _preferredByClrType.GetValueOrDefault(clrType);
    if (clrType == typeof(byte[]))
      return FindSpecialType(DbSpecialType.Binary, DbSpecialType.BinaryUnlimited, unlimited || size <= 0)
        ?? _preferredByClrType.GetValueOrDefault(clrType);
    return _preferredByClrType.GetValueOrDefault(clrType);
  }

  private DbTypeDef? FindSpecialType(DbSpecialType bounded, DbSpecialType unbounded, bool unlimited) {
    var wanted = unlimited ? unbounded : bounded;
    var fallback = unlimited ? bounded : unbounded;
    return FindSpecial(wanted) ?? FindSpecial(fallback);
  }

  // An enum is stored as its underlying integer.
  private static Type GetUnderlyingType(EntityMemberInfo member) {
    // An array member is looked up as an array of its items, whether it is declared an array or a List.
    if (member.ArrayItemType != null)
      return member.ArrayItemType.MakeArrayType();
    var type = member.DataType;
    return type.IsEnum ? Enum.GetUnderlyingType(type) : type;
  }
}
