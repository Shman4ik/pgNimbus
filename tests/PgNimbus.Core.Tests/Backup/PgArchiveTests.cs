using System.Text;
using PgNimbus.Core.Backup;

namespace PgNimbus.Core.Tests.Backup;

/// <summary>
/// The restore window reads two things before anything runs: what kind of file
/// it was given (only pg_dump's archives are restored, never a script, which
/// would need psql and the shell commands psql runs), and what the archive
/// holds. The listing below is pg_restore 18.6's real output for the live
/// tests' shop database.
/// </summary>
public class PgArchiveTests
{
    private const string ShopListing = """
        ;
        ; Archive created at 2026-10-10 09:32:12
        ;     dbname: shop
        ;     TOC Entries: 33
        ;     Compression: gzip
        ;     Dump Version: 1.16-0
        ;     Format: CUSTOM
        ;     Integer: 4 bytes
        ;     Offset: 8 bytes
        ;     Dumped from database version: 17.11 (Debian 17.11-1.pgdg13+2)
        ;     Dumped by pg_dump version: 18.6
        ;
        ;
        ; Selected TOC Entries:
        ;
        6; 2615 16388 SCHEMA - Odd "Schema" *? app
        7; 2615 16387 SCHEMA - sales app
        3469; 0 0 ACL - SCHEMA sales app
        225; 1259 16418 TABLE Odd "Schema" *? Weird.Table app
        220; 1259 16390 TABLE public customers app
        227; 1259 16434 VIEW public active app
        219; 1259 16389 SEQUENCE public customers_id_seq app
        3470; 0 0 SEQUENCE OWNED BY public customers_id_seq app
        223; 1259 16410 TABLE sales events app
        224; 1259 16413 TABLE sales events_2026 app
        222; 1259 16399 TABLE sales orders app
        226; 1259 16423 MATERIALIZED VIEW sales order_totals reporting
        3301; 0 0 TABLE ATTACH sales events_2026 app
        3461; 0 16418 TABLE DATA Odd "Schema" *? Weird.Table app
        3457; 0 16390 TABLE DATA public customers app
        3460; 0 16413 TABLE DATA sales events_2026 app
        3459; 0 16399 TABLE DATA sales orders app
        3475; 0 0 SEQUENCE SET public customers_id_seq app
        3308; 2606 16405 FK CONSTRAINT sales orders orders_customer_id_fkey app
        3462; 0 16423 MATERIALIZED VIEW DATA sales order_totals reporting
        """
        // An entry with no owner ends in a space, which a raw literal would trim.
        + "\n3463; 0 0 COMMENT - EXTENSION plpgsql ";

    [Test]
    public async Task The_header_says_what_was_saved_and_by_which_versions()
    {
        var listing = PgArchiveListing.Parse(ShopListing);

        await Assert.That(listing.DatabaseName).IsEqualTo("shop");
        await Assert.That(listing.CreatedAt).IsEqualTo("2026-10-10 09:32:12");
        await Assert.That(listing.Format).IsEqualTo("CUSTOM");
        await Assert.That(listing.DumpedFrom).IsEqualTo(new PgVersion(17, 11));
        await Assert.That(listing.DumpedBy).IsEqualTo(new PgVersion(18, 6));
    }

    [Test]
    public async Task Entries_are_counted_by_kind_and_the_owners_are_read_off_the_end()
    {
        var listing = PgArchiveListing.Parse(ShopListing);

        await Assert.That(listing.Entries.Count).IsEqualTo(21);
        await Assert.That(listing.TableCount).IsEqualTo(5);
        await Assert.That(listing.TableDataCount).IsEqualTo(4);
        await Assert.That(listing.HasRows).IsTrue();
        await Assert.That(listing.Owners).IsEquivalentTo(["app", "reporting"], CollectionOrdering.Matching);
        // The longest kind wins: TABLE DATA is not a TABLE named DATA.
        await Assert.That(listing.Entries.Count(e => e.Description == "MATERIALIZED VIEW DATA")).IsEqualTo(1);
        // An entry with no owner ends in a space and adds none.
        await Assert.That(listing.Entries.Single(e => e.Id == 3463).Owner).IsEqualTo("");
    }

    [Test]
    public async Task A_structure_only_archive_has_no_rows()
    {
        var listing = PgArchiveListing.Parse("""
            ;     dbname: shop
            220; 1259 16390 TABLE public customers app
            3305; 2606 16397 CONSTRAINT public customers customers_pkey app
            """);

        await Assert.That(listing.HasRows).IsFalse();
        await Assert.That(listing.TableCount).IsEqualTo(1);
    }

    [Test]
    public async Task Archives_are_told_apart_from_scripts_by_their_first_bytes()
    {
        await Assert.That(PgArchive.Detect("PGDMP\u0001\u0010\u0000"u8)).IsEqualTo(PgArchiveFormat.Custom);

        var tar = new byte[512];
        "toc.dat"u8.CopyTo(tar);
        "ustar"u8.CopyTo(tar.AsSpan(257));
        await Assert.That(PgArchive.Detect(tar)).IsEqualTo(PgArchiveFormat.Tar);

        await Assert.That(PgArchive.Detect("--\n-- PostgreSQL database dump\n--\n"u8)).IsEqualTo(PgArchiveFormat.PlainSql);
        await Assert.That(PgArchive.Detect("\\restrict abc\n"u8)).IsEqualTo(PgArchiveFormat.PlainSql);
        // A byte-order mark doesn't hide a script (it hid psql meta-commands from
        // pgAdmin's filter, CVE-2025-13780).
        await Assert.That(PgArchive.Detect(Encoding.UTF8.GetPreamble().Concat("-- dump"u8.ToArray()).ToArray())).IsEqualTo(PgArchiveFormat.PlainSql);

        await Assert.That(PgArchive.Detect("PK\u0003\u0004 a zip"u8)).IsEqualTo(PgArchiveFormat.Unknown);
        await Assert.That(PgArchive.Detect([0x00, 0x01, 0x02])).IsEqualTo(PgArchiveFormat.Unknown);
        await Assert.That(PgArchive.Detect(ReadOnlySpan<byte>.Empty)).IsEqualTo(PgArchiveFormat.Unknown);
    }

    [Test]
    public async Task A_file_that_is_not_there_is_missing_and_a_directory_archive_has_a_toc()
    {
        var root = Path.Combine(Path.GetTempPath(), "pgnimbus-archive-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await Assert.That(PgArchive.Detect(Path.Combine(root, "nope.dump"))).IsEqualTo(PgArchiveFormat.Missing);
            await Assert.That(PgArchive.Detect(root)).IsEqualTo(PgArchiveFormat.Unknown);
            await File.WriteAllTextAsync(Path.Combine(root, "toc.dat"), "x");
            await Assert.That(PgArchive.Detect(root)).IsEqualTo(PgArchiveFormat.Directory);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
