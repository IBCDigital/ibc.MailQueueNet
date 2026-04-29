//-----------------------------------------------------------------------
// <copyright file="JsonTreeNode.cs" company="IBC Digital">
//   Copyright (c) IBC Digital. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

namespace MailFunk.Models
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// Represents a node in a JSON tree used for rendering nested merge-row payloads.
    /// </summary>
    public sealed class JsonTreeNode
    {
        /// <summary>
        /// Initialises a new instance of the <see cref="JsonTreeNode"/> class.
        /// </summary>
        /// <param name="name">The node name (property name or array index label).</param>
        /// <param name="value">The node value (for leaf nodes).</param>
        /// <param name="children">Optional child nodes.</param>
        public JsonTreeNode(string name, string? value, IReadOnlyList<JsonTreeNode>? children = null)
        {
            this.Name = name ?? string.Empty;
            this.Value = value;
            this.Children = children ?? Array.Empty<JsonTreeNode>();
        }

        /// <summary>
        /// Gets the node name.
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// Gets the node value for leaf nodes.
        /// </summary>
        public string? Value { get; }

        /// <summary>
        /// Gets the child nodes.
        /// </summary>
        public IReadOnlyList<JsonTreeNode> Children { get; }

        /// <summary>
        /// Gets a value indicating whether this node has children.
        /// </summary>
        public bool HasChildren
        {
            get
            {
                return this.Children.Count > 0;
            }
        }

        /// <summary>
        /// Gets a display string suitable for UI rendering.
        /// </summary>
        public string DisplayText
        {
            get
            {
                if (this.HasChildren)
                {
                    return this.Name;
                }

                if (string.IsNullOrWhiteSpace(this.Value))
                {
                    return this.Name;
                }

                return this.Name + ": " + this.Value;
            }
        }
    }
}
