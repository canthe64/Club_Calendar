using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;

namespace FacilityScheduler.Components.Calendar;

/// <summary>
/// Renders notes text with its http(s) URLs as links (NoteLinks.Split), opening in a new tab.
/// Plain text goes through Blazor's normal encoding, so notes can't inject markup. Built as a
/// render-tree class rather than .razor so no template whitespace ends up inside the caller's
/// <c>white-space:pre-wrap</c> span.
/// </summary>
public sealed class LinkifiedText : ComponentBase
{
    [Parameter] public string? Text { get; set; }

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        foreach (var segment in NoteLinks.Split(Text))
        {
            if (!segment.IsUrl)
            {
                builder.AddContent(0, segment.Text);
                continue;
            }

            builder.OpenElement(1, "a");
            builder.AddAttribute(2, "href", segment.Text);
            builder.AddAttribute(3, "target", "_blank");
            builder.AddAttribute(4, "rel", "noopener noreferrer");
            builder.AddAttribute(5, "style", "color:#2b6cb0;text-decoration:underline");
            builder.AddContent(6, segment.Text);
            builder.CloseElement();
        }
    }
}
