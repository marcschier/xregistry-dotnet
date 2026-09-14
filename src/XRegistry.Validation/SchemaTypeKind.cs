namespace XRegistry.Validation;

/// <summary>The kind of concrete declaration selected from a schema Document.</summary>
public enum SchemaTypeKind
{
    /// <summary>An object in a JSON Schema schema position.</summary>
    JsonSchema,

    /// <summary>A JSON Structure type declaration.</summary>
    JsonStructure,

    /// <summary>An Avro record declaration, not an enum or primitive.</summary>
    AvroRecord,

    /// <summary>A Protobuf message declaration, not an enum.</summary>
    ProtobufMessage,

    /// <summary>A named XML Schema simple or complex type.</summary>
    XmlSchemaType,

    /// <summary>An XML Schema element declaration.</summary>
    XmlSchemaElement,
}
