using System.Globalization;
using System.Net;
using System.Text;
using RaidDebrief.Core;

namespace RaidDebrief.UI;

public sealed class SvgArenaRenderer
{
    private const float PlayerMarkerRadius = 8;
    private readonly StringBuilder builder = new(16_384);

    public SvgArenaRenderer(int width = 900, int height = 900, int padding = 48)
    {
        if (width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        if (height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height));
        }

        if (padding < 0 || padding * 2 >= width || padding * 2 >= height)
        {
            throw new ArgumentOutOfRangeException(nameof(padding));
        }

        this.Width = width;
        this.Height = height;
        this.Padding = padding;
    }

    public int Width { get; }

    public int Height { get; }

    public int Padding { get; }

    public string Render(ArenaRenderScene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);

        var viewport = this.CalculateArenaViewport(scene.WorldBounds);
        this.builder.Clear();
        this.builder.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 ")
            .Append(this.Width)
            .Append(' ')
            .Append(this.Height)
            .Append("\" role=\"img\" aria-label=\"Raid replay at ")
            .Append(scene.TimestampMilliseconds)
            .Append(" milliseconds\">");
        this.AppendStyle();
        this.builder.Append("<rect class=\"background\" width=\"100%\" height=\"100%\"/>");
        this.AppendArena(viewport, scene.Shape, scene.BoundsKind);
        this.AppendWaymarks(scene.Waymarks, viewport);
        this.AppendActors(scene.Actors, scene.WorldBounds, viewport);
        this.AppendTargetMarkers(scene.TargetMarkers, viewport);
        this.builder.Append("<text class=\"timestamp\" x=\"24\" y=\"34\">")
            .Append(FormatTimestamp(scene.TimestampMilliseconds))
            .Append("</text></svg>");
        return this.builder.ToString();
    }

    private static string FormatTimestamp(long timestampMilliseconds)
    {
        var minutes = timestampMilliseconds / 60_000;
        var seconds = (timestampMilliseconds % 60_000) / 1_000;
        var milliseconds = timestampMilliseconds % 1_000;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{minutes:00}:{seconds:00}.{milliseconds:000}");
    }

    private ArenaViewport CalculateArenaViewport(ArenaBounds bounds)
    {
        var availableWidth = this.Width - (this.Padding * 2f);
        var availableHeight = this.Height - (this.Padding * 2f);
        var scale = MathF.Min(availableWidth / bounds.Width, availableHeight / bounds.Depth);
        var width = bounds.Width * scale;
        var height = bounds.Depth * scale;
        return new ArenaViewport(
            (this.Width - width) / 2,
            (this.Height - height) / 2,
            width,
            height);
    }

    private void AppendStyle()
    {
        this.builder.Append(
            "<style>" +
            ".background{fill:#10141c}.arena{fill:#172638;stroke:#8994a6;stroke-width:2}" +
            ".arena-authoritative{fill:#7a6110}" +
            ".grid{stroke:#475266;stroke-width:1;opacity:.55}.facing{stroke:#f8fafc;stroke-width:2;stroke-linecap:round}" +
            ".target-circle{fill:none;opacity:.72}.target-player{stroke:#42a5f5}.target-battle-npc{stroke:#ef5350}" +
            ".player{fill:#42a5f5;stroke:#e3f2fd;stroke-width:2}.battle-npc{fill:#ef5350;stroke:#ffebee;stroke-width:2}" +
            ".dead{fill:#606874;stroke:#c7ccd4}.untargetable{opacity:.55;stroke-dasharray:3 2}" +
            ".marker-label{fill:#fff;font:12px sans-serif;text-anchor:middle;paint-order:stroke;stroke:#10141c;stroke-width:3px}" +
            ".waymark{fill:#ffd54f;stroke:#fff8e1;stroke-width:2}.waymark-label{fill:#241b00;font:bold 13px sans-serif;text-anchor:middle;dominant-baseline:central}" +
            ".target-marker{fill:#fff4a8;font:bold 14px sans-serif;text-anchor:middle;paint-order:stroke;stroke:#10141c;stroke-width:4px}" +
            ".timestamp{fill:#e7ecf4;font:bold 18px monospace}.dead-cross{stroke:#fff;stroke-width:2}" +
            "</style>");
    }

    private void AppendArena(
        ArenaViewport viewport,
        ArenaShape shape,
        ArenaBoundsKind boundsKind)
    {
        var boundsClass = boundsKind == ArenaBoundsKind.Authoritative
            ? " arena-authoritative"
            : " arena-generic";
        if (shape == ArenaShape.Circle)
        {
            var centerX = viewport.X + (viewport.Width / 2);
            var centerY = viewport.Y + (viewport.Height / 2);
            var radius = MathF.Min(viewport.Width, viewport.Height) / 2;
            this.builder.Append("<circle class=\"arena arena-circle")
                .Append(boundsClass)
                .Append("\" cx=\"");
            AppendNumber(this.builder, centerX);
            this.builder.Append("\" cy=\"");
            AppendNumber(this.builder, centerY);
            this.builder.Append("\" r=\"");
            AppendNumber(this.builder, radius);
            this.builder.Append("\"/>");
            for (var division = 1; division < 4; division++)
            {
                this.builder.Append("<circle class=\"grid\" fill=\"none\" cx=\"");
                AppendNumber(this.builder, centerX);
                this.builder.Append("\" cy=\"");
                AppendNumber(this.builder, centerY);
                this.builder.Append("\" r=\"");
                AppendNumber(this.builder, radius * division / 4f);
                this.builder.Append("\"/>");
            }

            this.builder.Append("<path class=\"grid\" d=\"M ");
            AppendNumber(this.builder, centerX - radius);
            this.builder.Append(' ');
            AppendNumber(this.builder, centerY);
            this.builder.Append(" H ");
            AppendNumber(this.builder, centerX + radius);
            this.builder.Append(" M ");
            AppendNumber(this.builder, centerX);
            this.builder.Append(' ');
            AppendNumber(this.builder, centerY - radius);
            this.builder.Append(" V ");
            AppendNumber(this.builder, centerY + radius);
            this.builder.Append("\"/>");
            return;
        }

        this.builder.Append("<rect class=\"arena")
            .Append(boundsClass)
            .Append("\" x=\"");
        AppendNumber(this.builder, viewport.X);
        this.builder.Append("\" y=\"");
        AppendNumber(this.builder, viewport.Y);
        this.builder.Append("\" width=\"");
        AppendNumber(this.builder, viewport.Width);
        this.builder.Append("\" height=\"");
        AppendNumber(this.builder, viewport.Height);
        this.builder.Append("\"/>");

        for (var division = 1; division < 4; division++)
        {
            var fraction = division / 4f;
            var x = viewport.X + (viewport.Width * fraction);
            var y = viewport.Y + (viewport.Height * fraction);
            this.builder.Append("<path class=\"grid\" d=\"M ");
            AppendNumber(this.builder, x);
            this.builder.Append(' ');
            AppendNumber(this.builder, viewport.Y);
            this.builder.Append(" V ");
            AppendNumber(this.builder, viewport.Y + viewport.Height);
            this.builder.Append(" M ");
            AppendNumber(this.builder, viewport.X);
            this.builder.Append(' ');
            AppendNumber(this.builder, y);
            this.builder.Append(" H ");
            AppendNumber(this.builder, viewport.X + viewport.Width);
            this.builder.Append("\"/>");
        }
    }

    private void AppendWaymarks(ReadOnlySpan<ArenaWaymarkMarker> waymarks, ArenaViewport viewport)
    {
        this.builder.Append("<g id=\"waymarks\">");
        foreach (ref readonly var waymark in waymarks)
        {
            var position = viewport.Project(waymark.Position);
            const float radius = 12;
            this.builder.Append("<g data-waymark-id=\"")
                .Append(waymark.Id)
                .Append("\"><path class=\"waymark\" d=\"M ");
            AppendNumber(this.builder, position.X);
            this.builder.Append(' ');
            AppendNumber(this.builder, position.Y - radius);
            this.builder.Append(" L ");
            AppendNumber(this.builder, position.X + radius);
            this.builder.Append(' ');
            AppendNumber(this.builder, position.Y);
            this.builder.Append(" L ");
            AppendNumber(this.builder, position.X);
            this.builder.Append(' ');
            AppendNumber(this.builder, position.Y + radius);
            this.builder.Append(" L ");
            AppendNumber(this.builder, position.X - radius);
            this.builder.Append(' ');
            AppendNumber(this.builder, position.Y);
            this.builder.Append(" Z\"/><text class=\"waymark-label\" x=\"");
            AppendNumber(this.builder, position.X);
            this.builder.Append("\" y=\"");
            AppendNumber(this.builder, position.Y);
            this.builder.Append("\">")
                .Append(WaymarkLabel(waymark.Id))
                .Append("</text></g>");
        }

        this.builder.Append("</g>");
    }

    private void AppendActors(
        ReadOnlySpan<ArenaActorMarker> actors,
        ArenaBounds worldBounds,
        ArenaViewport viewport)
    {
        this.builder.Append("<g id=\"actors\">");
        foreach (ref readonly var actor in actors)
        {
            var position = viewport.Project(actor.Position);
            var radius = actor.Kind == ArenaActorMarkerKind.Player ? PlayerMarkerRadius : 12f;
            var facingLength = radius + 10;
            var className = actor.Kind == ArenaActorMarkerKind.Player ? "player" : "battle-npc";
            this.builder.Append("<g data-stable-actor-id=\"")
                .Append(actor.Actor.StableActorId)
                .Append("\" data-kind=\"")
                .Append(actor.Kind)
                .Append("\">");

            this.builder.Append("<line class=\"facing\" x1=\"");
            AppendNumber(this.builder, position.X);
            this.builder.Append("\" y1=\"");
            AppendNumber(this.builder, position.Y);
            this.builder.Append("\" x2=\"");
            AppendNumber(this.builder, position.X + (actor.Facing.X * facingLength));
            this.builder.Append("\" y2=\"");
            AppendNumber(this.builder, position.Y + (actor.Facing.Y * facingLength));
            this.builder.Append("\"/><circle class=\"")
                .Append(className);
            if (actor.IsDead)
            {
                this.builder.Append(" dead");
            }

            if (!actor.IsTargetable)
            {
                this.builder.Append(" untargetable");
            }

            this.builder.Append("\" cx=\"");
            AppendNumber(this.builder, position.X);
            this.builder.Append("\" cy=\"");
            AppendNumber(this.builder, position.Y);
            this.builder.Append("\" r=\"");
            AppendNumber(this.builder, radius);
            this.builder.Append("\"/>");
            var targetCircleOuterRadius = actor.HitboxRadius * MathF.Min(
                viewport.Width / worldBounds.Width,
                viewport.Height / worldBounds.Depth);
            if (targetCircleOuterRadius > 0)
            {
                var targetCircleStrokeWidth = MathF.Min(2, targetCircleOuterRadius * 2);
                var targetCircleCenterRadius = targetCircleOuterRadius - (targetCircleStrokeWidth / 2);
                this.builder.Append("<circle class=\"target-circle ")
                    .Append(actor.Kind == ArenaActorMarkerKind.Player ? "target-player" : "target-battle-npc")
                    .Append("\" cx=\"");
                AppendNumber(this.builder, position.X);
                this.builder.Append("\" cy=\"");
                AppendNumber(this.builder, position.Y);
                this.builder.Append("\" r=\"");
                AppendNumber(this.builder, targetCircleCenterRadius);
                this.builder.Append("\" stroke-width=\"");
                AppendNumber(this.builder, targetCircleStrokeWidth);
                this.builder.Append("\"/>");
            }
            if (actor.IsDead)
            {
                this.AppendDeadCross(position, radius);
            }

            this.builder.Append("<text class=\"marker-label\" x=\"");
            AppendNumber(this.builder, position.X);
            this.builder.Append("\" y=\"");
            AppendNumber(this.builder, position.Y + radius + 15);
            this.builder.Append("\">");
            AppendActorLabel(this.builder, actor);
            this.builder.Append("</text></g>");
        }

        this.builder.Append("</g>");
    }

    private void AppendTargetMarkers(
        ReadOnlySpan<ArenaTargetMarker> targetMarkers,
        ArenaViewport viewport)
    {
        this.builder.Append("<g id=\"target-markers\">");
        foreach (ref readonly var targetMarker in targetMarkers)
        {
            var position = viewport.Project(targetMarker.Position);
            this.builder.Append("<text class=\"target-marker\" data-target-marker-id=\"")
                .Append(targetMarker.Id)
                .Append("\" data-stable-actor-id=\"")
                .Append(targetMarker.StableActorId)
                .Append("\" x=\"");
            AppendNumber(this.builder, position.X);
            this.builder.Append("\" y=\"");
            AppendNumber(this.builder, position.Y - 22);
            this.builder.Append("\">")
                .Append(targetMarker.Id)
                .Append("</text>");
        }

        this.builder.Append("</g>");
    }

    private void AppendDeadCross(ArenaPoint position, float radius)
    {
        var offset = radius * .55f;
        this.builder.Append("<path class=\"dead-cross\" d=\"M ");
        AppendNumber(this.builder, position.X - offset);
        this.builder.Append(' ');
        AppendNumber(this.builder, position.Y - offset);
        this.builder.Append(" L ");
        AppendNumber(this.builder, position.X + offset);
        this.builder.Append(' ');
        AppendNumber(this.builder, position.Y + offset);
        this.builder.Append(" M ");
        AppendNumber(this.builder, position.X + offset);
        this.builder.Append(' ');
        AppendNumber(this.builder, position.Y - offset);
        this.builder.Append(" L ");
        AppendNumber(this.builder, position.X - offset);
        this.builder.Append(' ');
        AppendNumber(this.builder, position.Y + offset);
        this.builder.Append("\"/>");
    }

    private static void AppendActorLabel(StringBuilder builder, in ArenaActorMarker actor)
    {
        const string playerPrefix = "Player ";
        if (actor.Kind == ArenaActorMarkerKind.Player
            && actor.Actor.Name.StartsWith(playerPrefix, StringComparison.Ordinal)
            && int.TryParse(
                actor.Actor.Name.AsSpan(playerPrefix.Length),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var playerNumber))
        {
            builder.Append('P').Append(playerNumber);
            return;
        }

        builder.Append(WebUtility.HtmlEncode(actor.Actor.Name));
    }

    private static string WaymarkLabel(WaymarkId id) => id switch
    {
        WaymarkId.One => "1",
        WaymarkId.Two => "2",
        WaymarkId.Three => "3",
        WaymarkId.Four => "4",
        _ => id.ToString(),
    };

    private static void AppendNumber(StringBuilder builder, float value) =>
        builder.Append(value.ToString("0.###", CultureInfo.InvariantCulture));

    private readonly record struct ArenaViewport(float X, float Y, float Width, float Height)
    {
        public ArenaPoint Project(ArenaPoint point) =>
            new(this.X + (point.X * this.Width), this.Y + (point.Y * this.Height));
    }
}
