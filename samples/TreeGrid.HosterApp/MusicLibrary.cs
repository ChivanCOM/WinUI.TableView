// A real music collection, read from the dump the RPM side writes (one tab-separated row per
// tagged file), shaped into the tree the import queue shows: artist, then album, then tracks.
//
// Generated data is uniform, and uniform data flatters a grid. Every artist the same size, every
// album the same size, every name the same length, nothing missing. A real collection is none of
// those: one artist owns a thousand tracks and eight hundred own one, a quarter of the files name
// no album at all and land in a single bucket together, and the longest title is a hundred and
// ninety characters of guest credits. Those are the shapes worth measuring against.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace TreeGrid.HosterApp;

/// <summary>One tagged file, as the dump recorded it.</summary>
public sealed class MusicTrack
{
    public string RelativePath = "";
    public string Artist = "";
    public string AlbumArtist = "";
    public string Album = "";
    public string Title = "";
    public int TrackNumber;
    public int DiscNumber;
    public int Year;
    public string Genre = "";
    public double DurationSeconds;
    public int BitrateKbps;
    public int SampleRate;
    public int BitDepth;
    public int Channels;
    public string Codec = "";
    public long SizeBytes;
    public int Availability;

    public string FileName => Path.GetFileName(RelativePath);
    public string Folder => Path.GetDirectoryName(RelativePath) is { Length: > 0 } d ? d : "";

    /// <summary>The name the tree shows for a track: its title, or the file when it has none —
    /// which is what a queue full of untagged files actually looks like.</summary>
    public string DisplayName => Title.Length > 0 ? Title : FileName;

    /// <summary>Whose shelf this goes on. Album artist wins, artist second, and what is left is a
    /// bucket — a real one, holding hundreds of rows, not a tidy demo group.</summary>
    public string GroupArtist =>
        AlbumArtist.Length > 0 ? AlbumArtist : Artist.Length > 0 ? Artist : UnknownArtist;

    public string GroupAlbum => Album.Length > 0 ? Album : UnknownAlbum;

    public const string UnknownArtist = "Unknown artist";
    public const string UnknownAlbum = "Unknown album";
}

/// <summary>The collection, grouped the way the grid groups it.</summary>
public sealed class MusicLibrary
{
    public required IReadOnlyList<MusicTrack> Tracks { get; init; }

    /// <summary>Artist → its albums, each with the tracks it owns, both in display order.</summary>
    public required IReadOnlyList<(string Artist, IReadOnlyList<(string Album, MusicTrack[] Tracks)> Albums)> Artists { get; init; }

    public int TrackCount => Tracks.Count;
    public int AlbumCount => Artists.Sum(a => a.Albums.Count);

    public static MusicLibrary Load(string path, int limit = int.MaxValue)
    {
        var tracks = new List<MusicTrack>();

        foreach (var line in File.ReadLines(path))
        {
            if (tracks.Count >= limit)
            {
                break;
            }

            var f = line.Split('\t');
            if (f.Length < 17)
            {
                continue;
            }

            tracks.Add(new MusicTrack
            {
                RelativePath = f[0],
                Artist = f[1],
                AlbumArtist = f[2],
                Album = f[3],
                Title = f[4],
                TrackNumber = Int(f[5]),
                DiscNumber = Int(f[6]),
                Year = Int(f[7]),
                Genre = f[8],
                DurationSeconds = Dbl(f[9]),
                BitrateKbps = Int(f[10]),
                SampleRate = Int(f[11]),
                BitDepth = Int(f[12]),
                Channels = Int(f[13]),
                Codec = f[14],
                SizeBytes = Lng(f[15]),
                Availability = Int(f[16]),
            });
        }

        // Same ordering the queue uses: artist, then album, then disc/track/title within it, so a
        // page fetched by offset returns what the grid would show at that offset.
        var artists = tracks
            .GroupBy(t => t.GroupArtist, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => (
                Artist: g.Key,
                Albums: (IReadOnlyList<(string, MusicTrack[])>)g
                    .GroupBy(t => t.GroupAlbum, StringComparer.OrdinalIgnoreCase)
                    .OrderBy(a => a.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(a => (a.Key, a
                        .OrderBy(t => t.DiscNumber)
                        .ThenBy(t => t.TrackNumber == 0 ? 1 : 0)
                        .ThenBy(t => t.TrackNumber)
                        .ThenBy(t => t.DisplayName, StringComparer.OrdinalIgnoreCase)
                        .ToArray()))
                    .ToList()))
            .ToList();

        return new MusicLibrary { Tracks = tracks, Artists = artists };
    }

    private static int Int(string s) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
    private static long Lng(string s) => long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
    private static double Dbl(string s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
}
