//-----------------------------------------------------------------------
// <copyright file="JsonTreeViewItem.razor.cs" company="IBC Digital">
//   Copyright (c) IBC Digital. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

namespace MailFunk.Components.Shared
{
    using MailFunk.Models;
    using Microsoft.AspNetCore.Components;

    /// <summary>
    /// Renders a single <see cref="JsonTreeNode"/> (and any children) as a MudBlazor tree item.
    /// </summary>
    public sealed partial class JsonTreeViewItem
    {
        /// <summary>
        /// Gets or sets the node to render.
        /// </summary>
        [Parameter]
        public JsonTreeNode Node { get; set; } = new JsonTreeNode(string.Empty, value: null);
    }
}
