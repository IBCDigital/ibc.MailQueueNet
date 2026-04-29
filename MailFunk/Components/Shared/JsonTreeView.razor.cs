//-----------------------------------------------------------------------
// <copyright file="JsonTreeView.razor.cs" company="IBC Digital">
//   Copyright (c) IBC Digital. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

namespace MailFunk.Components.Shared
{
    using System;
    using System.Collections.Generic;
    using MailFunk.Models;
    using Microsoft.AspNetCore.Components;

    /// <summary>
    /// Renders a collection of <see cref="JsonTreeNode"/> instances using a MudBlazor tree view.
    /// </summary>
    public sealed partial class JsonTreeView
    {
        /// <summary>
        /// Gets or sets the root nodes to render.
        /// </summary>
        [Parameter]
        public IReadOnlyList<JsonTreeNode> Nodes { get; set; } = Array.Empty<JsonTreeNode>();
    }
}
