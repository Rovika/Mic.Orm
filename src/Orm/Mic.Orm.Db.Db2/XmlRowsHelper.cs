using System.Globalization;
using System.Text;
using System.Xml;
using Mic.Orm.Db.Provider;

namespace Mic.Orm.Db.Db2;

/// <summary>Writes the rows of a parameter as an XML document, the form <see cref="Db2SqlDialect.JsonRowsSource"/>
/// reads back typed with <c>XMLTABLE</c>.</summary>
/// <remarks>Db2's <c>JSON_TABLE</c> spreads no array into rows, so the rows travel as XML: a root <c>r</c>, an element
/// <c>i</c> per row and, in it, an element per column named by the column's position, <c>c0</c>, <c>c1</c> and so
/// on. A null value has no element, which <c>XMLTABLE</c> reads as null.</remarks>
public static class XmlRowsHelper {
  /// <summary>Specifies the name of the document's root element.</summary>
  public const string RootName = "r";

  /// <summary>Specifies the name of a row's element.</summary>
  public const string RowName = "i";

  /// <summary>Specifies what a column's element is named with, before the column's position.</summary>
  public const string ColumnPrefix = "c";

  /// <summary>Serializes rows as an XML document, one element per row.</summary>
  /// <param name="provider">The provider, which decides the bytes a Guid travels as.</param>
  /// <param name="columns">The columns of a row, in order.</param>
  /// <param name="rows">The rows, each an array with one value per column.</param>
  /// <returns>The XML text, with no declaration.</returns>
  /// <exception cref="ArgumentException">A row has another number of values than there are columns.</exception>
  /// <remarks>A Guid and a binary value are written as hex, a Boolean as 1 or 0, a date and time as ISO 8601 to
  /// microseconds, an enum as its number.</remarks>
  public static string Serialize(DbProvider provider, IReadOnlyList<JsonRowsColumn> columns,
      IEnumerable<object?[]> rows) {
    ArgumentNullException.ThrowIfNull(provider);
    ArgumentNullException.ThrowIfNull(columns);
    ArgumentNullException.ThrowIfNull(rows);
    var text = new StringBuilder();
    var settings = new XmlWriterSettings { OmitXmlDeclaration = true, ConformanceLevel = ConformanceLevel.Document };
    using (var writer = XmlWriter.Create(text, settings)) {
      writer.WriteStartElement(RootName);
      foreach (var row in rows) {
        if (row.Length != columns.Count)
          throw new ArgumentException(
            $"A row has {row.Length} value(s) and {columns.Count} column(s) are declared.", nameof(rows));
        writer.WriteStartElement(RowName);
        for (var i = 0; i < row.Length; i++)
          if (row[i] != null)
            writer.WriteElementString(ColumnPrefix + i.ToString(CultureInfo.InvariantCulture),
              GetValueText(provider, row[i]!));
        writer.WriteEndElement();
      }
      writer.WriteEndElement();
    }
    return text.ToString();
  }

  private static string GetValueText(DbProvider provider, object value) => value switch {
    Guid guid => Convert.ToHexString(provider.AdaptParameterValue(guid) as byte[] ?? guid.ToByteArray()),
    byte[] bytes => Convert.ToHexString(bytes),
    string text => text,
    bool flag => flag ? "1" : "0",
    DateTime dateTime => dateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff", CultureInfo.InvariantCulture),
    DateOnly date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
    TimeOnly time => time.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
    Enum member => Convert.ToInt64(member, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
    double number => number.ToString("R", CultureInfo.InvariantCulture),
    float number => number.ToString("R", CultureInfo.InvariantCulture),
    _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
  };
}
