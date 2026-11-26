using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PhoneDesk.Audit;
using PhoneDesk.Services;

namespace PhoneDesk.Tests
{
    /// <summary>
    /// Exercises the file-based audit sink: JSON-lines round-trip, per-tenant file naming, secret
    /// redaction on disk (issue #67 acceptance criterion), ordering, and rotation.
    /// </summary>
    public sealed class FileAuditLogTests : IDisposable
    {
        private const string SampleJwt =
            "eyJhbGciOiJSUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.SflKxwRJSMeKKF2QT4fwpMeJf36POk6yJV_adQssw5c";

        private readonly string _root;

        public FileAuditLogTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "tpm-audit-tests", Guid.NewGuid().ToString("N"));
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_root))
                {
                    Directory.Delete(_root, recursive: true);
                }
            }
            catch (IOException)
            {
                // best-effort cleanup
            }
        }

        private static AuditRecord Record(string operation, string? tenantId = "tenant-1", AuditOutcome outcome = AuditOutcome.Success, AuditKind kind = AuditKind.Operation)
            => new()
            {
                TimestampUtc = DateTimeOffset.UtcNow,
                Operator = "admin@contoso.com",
                TenantId = tenantId,
                TenantName = "Contoso",
                Operation = operation,
                Target = "contoso-sales",
                Outcome = outcome,
                CorrelationId = Guid.NewGuid().ToString("N"),
                AppVersion = "Version 9.9.9",
                Kind = kind
            };

        [Fact]
        public void Append_ThenRead_RoundTripsRecord()
        {
            var log = new FileAuditLog(_root);
            log.Append(Record("Create Call Queue"));

            var all = log.Read();

            var record = Assert.Single(all);
            Assert.Equal("Create Call Queue", record.Operation);
            Assert.Equal("tenant-1", record.TenantId);
            Assert.Equal(AuditOutcome.Success, record.Outcome);
            Assert.Equal(AuditKind.Operation, record.Kind);
        }

        [Fact]
        public void Append_WritesJsonLines_OneObjectPerLine()
        {
            var log = new FileAuditLog(_root);
            log.Append(Record("Op A"));
            log.Append(Record("Op B"));

            var file = Directory.EnumerateFiles(_root, "*.jsonl", SearchOption.AllDirectories).Single();
            var lines = File.ReadAllLines(file).Where(l => !string.IsNullOrWhiteSpace(l)).ToArray();

            Assert.Equal(2, lines.Length);
            Assert.All(lines, l => Assert.StartsWith("{", l.TrimStart()));
        }

        [Fact]
        public void Append_UsesSeparateFilePerTenant()
        {
            var log = new FileAuditLog(_root);
            log.Append(Record("Op", tenantId: "tenant-A"));
            log.Append(Record("Op", tenantId: "tenant-B"));

            Assert.True(Directory.Exists(Path.Combine(_root, "tenant-A")));
            Assert.True(Directory.Exists(Path.Combine(_root, "tenant-B")));
        }

        [Fact]
        public void Append_NullTenant_UsesUnknownTenantFolder()
        {
            var log = new FileAuditLog(_root);
            log.Append(Record("Op", tenantId: null));

            Assert.True(Directory.Exists(Path.Combine(_root, "unknown-tenant")));
        }

        [Fact]
        public void Append_SanitizesTenantIdWithPathSeparators()
        {
            var log = new FileAuditLog(_root);
            log.Append(Record("Op", tenantId: "bad/../id"));

            // No traversal outside the root: path separators are replaced, so the sanitized folder
            // is a direct child of the root (its full path stays under the root).
            var dirs = Directory.EnumerateDirectories(_root).ToArray();
            Assert.Single(dirs);
            var fullRoot = Path.GetFullPath(_root);
            Assert.StartsWith(fullRoot, Path.GetFullPath(dirs[0]));
            var name = Path.GetFileName(dirs[0]);
            Assert.DoesNotContain(Path.DirectorySeparatorChar, name);
            Assert.DoesNotContain(Path.AltDirectorySeparatorChar, name);
        }

        [Fact]
        public void Read_ReturnsNewestFirst()
        {
            var log = new FileAuditLog(_root);
            var older = Record("Older") with { TimestampUtc = DateTimeOffset.UtcNow.AddMinutes(-10) };
            var newer = Record("Newer") with { TimestampUtc = DateTimeOffset.UtcNow };
            log.Append(older);
            log.Append(newer);

            var all = log.Read();

            Assert.Equal("Newer", all[0].Operation);
            Assert.Equal("Older", all[1].Operation);
        }

        [Fact]
        public void Read_SkipsMalformedLines()
        {
            var log = new FileAuditLog(_root);
            log.Append(Record("Good"));

            var file = Directory.EnumerateFiles(_root, "*.jsonl", SearchOption.AllDirectories).Single();
            File.AppendAllText(file, "this is not json" + Environment.NewLine);

            var all = log.Read();

            var record = Assert.Single(all);
            Assert.Equal("Good", record.Operation);
        }

        [Fact]
        public void Read_EmptyRoot_ReturnsEmpty()
        {
            var log = new FileAuditLog(_root);
            Assert.Empty(log.Read());
        }

        // Acceptance criterion: no access token or client secret ever appears in an audit file.
        [Fact]
        public void Append_NeverPersistsAccessTokenOrClientSecret()
        {
            var log = new FileAuditLog(_root);
            log.Append(new AuditRecord
            {
                Operation = "ConnectGraph",
                TenantId = "tenant-1",
                Operator = "admin@contoso.com",
                ErrorDetail = $"auth failure: {SampleJwt}",
                Parameters = new Dictionary<string, string>
                {
                    ["ClientSecret"] = "my-client-secret-value",
                    ["AccessToken"] = SampleJwt,
                    ["Bearer"] = "Bearer someOpaqueToken123"
                },
                CorrelationId = "abc",
                AppVersion = "Version 9.9.9"
            });

            var file = Directory.EnumerateFiles(_root, "*.jsonl", SearchOption.AllDirectories).Single();
            var contents = File.ReadAllText(file);

            Assert.DoesNotContain(SampleJwt, contents);
            Assert.DoesNotContain("eyJ", contents);
            Assert.DoesNotContain("my-client-secret-value", contents);
            Assert.DoesNotContain("someOpaqueToken123", contents);
            Assert.Contains(AuditRedactor.Placeholder, contents);
        }
    }
}
