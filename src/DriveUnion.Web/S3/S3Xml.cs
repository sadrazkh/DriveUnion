using System.Globalization;
using System.Text;
using System.Xml;
using DriveUnion.Core.Application;

namespace DriveUnion.Web.S3;

/// <summary>
/// The XML S3 speaks.
///
/// <para>Written by hand with <see cref="XmlWriter"/> rather than serialised from types. The shapes
/// are fixed by somebody else's protocol and are not ours to model: what matters is the element
/// names, the order, and the namespace — <c>http://s3.amazonaws.com/doc/2006-03-01/</c>, which every
/// client checks and which is on the document element and nowhere else.</para>
/// </summary>
public static class S3Xml
{
    public const string Namespace = "http://s3.amazonaws.com/doc/2006-03-01/";

    /// <summary>ISO-8601 to the millisecond with a <c>Z</c>, which is the only form S3 emits.</summary>
    private const string Timestamp = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

    public static string ListAllMyBuckets(string bucket, DateTimeOffset createdAt, string ownerId) =>
        Write(writer =>
        {
            writer.WriteStartElement("ListAllMyBucketsResult", Namespace);

            writer.WriteStartElement("Owner");
            writer.WriteElementString("ID", ownerId);
            writer.WriteElementString("DisplayName", bucket);
            writer.WriteEndElement();

            writer.WriteStartElement("Buckets");
            writer.WriteStartElement("Bucket");
            writer.WriteElementString("Name", bucket);
            writer.WriteElementString("CreationDate", Utc(createdAt));
            writer.WriteEndElement();
            writer.WriteEndElement();

            writer.WriteEndElement();
        });

    public static string ListObjectsV2(
        string bucket,
        S3Listing listing,
        string? prefix,
        string? delimiter,
        int maxKeys) =>
        Write(writer =>
        {
            ArgumentNullException.ThrowIfNull(listing);

            writer.WriteStartElement("ListBucketResult", Namespace);
            writer.WriteElementString("Name", bucket);
            writer.WriteElementString("Prefix", prefix ?? string.Empty);
            writer.WriteElementString("MaxKeys", maxKeys.ToString(CultureInfo.InvariantCulture));
            writer.WriteElementString("KeyCount", (listing.Objects.Count + listing.CommonPrefixes.Count)
                .ToString(CultureInfo.InvariantCulture));
            writer.WriteElementString("IsTruncated", listing.IsTruncated ? "true" : "false");

            if (delimiter is { Length: > 0 }) writer.WriteElementString("Delimiter", delimiter);

            if (listing.NextToken is { Length: > 0 } token)
            {
                writer.WriteElementString("NextContinuationToken", token);
            }

            foreach (var item in listing.Objects)
            {
                writer.WriteStartElement("Contents");
                writer.WriteElementString("Key", item.Key);
                writer.WriteElementString("LastModified", Utc(item.ModifiedAt));
                writer.WriteElementString("ETag", item.ETag);
                writer.WriteElementString("Size", item.SizeBytes.ToString(CultureInfo.InvariantCulture));

                // The one storage class this product has, named the way S3 names its default. A
                // client that switches on it must find something it knows.
                writer.WriteElementString("StorageClass", "STANDARD");
                writer.WriteEndElement();
            }

            foreach (var common in listing.CommonPrefixes)
            {
                writer.WriteStartElement("CommonPrefixes");
                writer.WriteElementString("Prefix", common);
                writer.WriteEndElement();
            }

            writer.WriteEndElement();
        });

    /// <summary>
    /// An S3 error document.
    ///
    /// <para>The <c>Code</c> is the contract: clients switch on it and users read it in a CLI's
    /// output, so it has to be one of the strings AWS defines rather than something descriptive of
    /// this product. The <c>Message</c> is where anything of ours goes.</para>
    /// </summary>
    public static string Error(string code, string message, string resource, string requestId) =>
        Write(writer =>
        {
            writer.WriteStartElement("Error", Namespace);
            writer.WriteElementString("Code", code);
            writer.WriteElementString("Message", message);
            writer.WriteElementString("Resource", resource);
            writer.WriteElementString("RequestId", requestId);
            writer.WriteEndElement();
        });

    private static string Utc(DateTimeOffset value) =>
        value.UtcDateTime.ToString(Timestamp, CultureInfo.InvariantCulture);

    private static string Write(Action<XmlWriter> body)
    {
        var output = new StringBuilder();

        using (var writer = XmlWriter.Create(output, new XmlWriterSettings
        {
            Indent = false,
            OmitXmlDeclaration = false,
            Encoding = Encoding.UTF8,
        }))
        {
            body(writer);
        }

        // XmlWriter over a StringBuilder writes utf-16 in the declaration whatever the Encoding
        // says, because a StringBuilder is utf-16. The bytes that go out are utf-8, and a client
        // that believed the declaration would decode them wrong.
        return output.ToString().Replace("utf-16", "utf-8", StringComparison.OrdinalIgnoreCase);
    }
}
