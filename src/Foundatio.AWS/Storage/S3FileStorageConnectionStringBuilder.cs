using System;
using Amazon.S3;

namespace Foundatio.Storage;

public class S3FileStorageConnectionStringBuilder : AmazonConnectionStringBuilder
{
    private string _bucket;
    private string _useChunkEncoding;
    private string _cannedAcl;

    public S3FileStorageConnectionStringBuilder()
    {
    }

    public S3FileStorageConnectionStringBuilder(string connectionString) : base(connectionString)
    {
    }

    public string Bucket
    {
        get => String.IsNullOrEmpty(_bucket) ? "storage" : _bucket;
        set => _bucket = value;
    }

    public bool? UseChunkEncoding
    {
        get => String.IsNullOrEmpty(_useChunkEncoding) ? null : (bool?)Convert.ToBoolean(_useChunkEncoding);
        set => _useChunkEncoding = value.ToString();
    }

    public S3CannedACL CannedACL
    {
        get => String.IsNullOrEmpty(_cannedAcl) ? null : S3CannedACL.FindValue(_cannedAcl);
        set => _cannedAcl = value.Value;
    }

    protected override bool ParseItem(string key, string value)
    {
        if (String.Equals(key, "Bucket", StringComparison.OrdinalIgnoreCase))
        {
            _bucket = value;
            return true;
        }
        if (String.Equals(key, "UseChunkEncoding", StringComparison.OrdinalIgnoreCase))
        {
            _useChunkEncoding = value;
            return true;
        }
        if (String.Equals(key, "CannedACL", StringComparison.OrdinalIgnoreCase) ||
            String.Equals(key, "S3CannedACL", StringComparison.OrdinalIgnoreCase))
        {
            _cannedAcl = value;
            return true;
        }

        return base.ParseItem(key, value);
    }

    public override string ToString()
    {
        string connectionString = base.ToString();
        if (!String.IsNullOrEmpty(_bucket))
            connectionString += "Bucket=" + Bucket + ";";
        if (!String.IsNullOrEmpty(_useChunkEncoding))
            connectionString += "UseChunkEncoding=" + UseChunkEncoding + ";";
        if (!String.IsNullOrEmpty(ServiceUrl))
            connectionString += "ServiceUrl=" + ServiceUrl + ";";
        if (!String.IsNullOrEmpty(CannedACL))
            connectionString += "CannedACL=" + CannedACL + ";";

        return connectionString;
    }
}
