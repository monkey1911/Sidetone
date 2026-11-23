// SPDX-License-Identifier: AGPL-3.0-only
/**
* Sidetone - an IP dispatch console for DVMProject FNE networks. Graphical front end.
*
* The boxes on a phone (the narrow layout): each becomes a full-screen sheet at full text size instead of
* the desktop box shrunk to fit. The card fills the screen below the header (square corners, no border);
* a labelled row (a two-column grid whose first column is a fixed width, like Settings' fields) puts its
* label above its control; a text box or drop-down with a fixed width fills the width. Rows of buttons are
* WrapPanels in the boxes themselves, so they wrap here and look the same on a desktop.
*
* Every change is remembered and undone when the layout goes wide again (a tablet turned sideways, a
* desktop window widened), so tablets and desktops get exactly the boxes they always had. Applied on each
* refresh while a box is open, so rows a box builds when it opens are adapted too.
*/

using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.LogicalTree;

namespace Sidetone.Gui
{
    internal static class PhoneSheet
    {
        /// <summary>The widest a field in a wrapping row gets on a phone (a 360 px screen, less the sheet's padding).</summary>
        private const double PhoneFieldWidth = 320;

        /// <summary>How to put each changed control back.</summary>
        private static readonly ConditionalWeakTable<Control, Action> undo = new ConditionalWeakTable<Control, Action>();
        /// <summary>The controls changed under each card.</summary>
        private static readonly ConditionalWeakTable<Control, List<Control>> changed = new ConditionalWeakTable<Control, List<Control>>();

        /// <summary>Makes a box's card a phone sheet (narrow) or puts it back as it was.</summary>
        public static void Apply(Control card, bool narrow)
        {
            List<Control> done = changed.GetOrCreateValue(card);
            if (!narrow)
            {
                foreach (Control c in done)
                    if (undo.TryGetValue(c, out Action back))
                    {
                        back();
                        undo.Remove(c);
                    }
                done.Clear();
                return;
            }

            if (card is Border b && !undo.TryGetValue(b, out _))
            {
                (double w, Thickness m, Thickness pad, Thickness edge, CornerRadius r, HorizontalAlignment h, VerticalAlignment v) =
                    (b.Width, b.Margin, b.Padding, b.BorderThickness, b.CornerRadius, b.HorizontalAlignment, b.VerticalAlignment);
                Change(b, done, () =>
                {
                    b.Width = double.NaN;
                    b.Margin = new Thickness(0);
                    b.Padding = new Thickness(16, 14, 16, 14);
                    b.BorderThickness = new Thickness(0);
                    b.CornerRadius = new CornerRadius(0);
                    b.HorizontalAlignment = HorizontalAlignment.Stretch;
                    b.VerticalAlignment = VerticalAlignment.Stretch;
                }, () =>
                {
                    b.Width = w; b.Margin = m; b.Padding = pad; b.BorderThickness = edge; b.CornerRadius = r;
                    b.HorizontalAlignment = h; b.VerticalAlignment = v;
                });
            }

            foreach (ILogical node in card.GetLogicalDescendants())
            {
                if (node is not Control c || undo.TryGetValue(c, out _))
                    continue;
                if (c is Grid g && IsLabelledRow(g))
                    Stack(g, done);
                else if ((c is TextBox || c is ComboBox) && !double.IsNaN(c.Width))
                {
                    double w = c.Width;
                    HorizontalAlignment h = c.HorizontalAlignment;
                    // in a row of its own it fills the width; in a wrapping row (which doesn't stretch) it's kept to what a phone shows
                    if (c.Parent is WrapPanel || c.Parent is StackPanel { Orientation: Orientation.Horizontal })
                    {
                        if (w > PhoneFieldWidth)
                            Change(c, done, () => c.Width = PhoneFieldWidth, () => c.Width = w);
                    }
                    else
                        Change(c, done, () => { c.Width = double.NaN; c.HorizontalAlignment = HorizontalAlignment.Stretch; },
                                        () => { c.Width = w; c.HorizontalAlignment = h; });
                }
            }
        }

        /// <summary>A label beside its control: two columns, the first a fixed width, one child in each, no rows.</summary>
        internal static bool IsLabelledRow(Grid g) =>
            g.ColumnDefinitions.Count == 2 && g.RowDefinitions.Count == 0 && g.ColumnDefinitions[0].Width.IsAbsolute &&
            g.ColumnDefinitions[1].Width.IsStar && g.Children.Count == 2 && g.Children[0] is TextBlock &&
            Grid.GetColumn(g.Children[0]) == 0 && Grid.GetColumn(g.Children[1]) == 1;

        /// <summary>Puts a labelled row's label above its control.</summary>
        private static void Stack(Grid g, List<Control> done)
        {
            Control label = g.Children[0], control = g.Children[1];
            ColumnDefinitions columns = g.ColumnDefinitions;
            Thickness controlMargin = control.Margin;
            Change(g, done, () =>
            {
                g.ColumnDefinitions = new ColumnDefinitions("*");
                g.RowDefinitions = new RowDefinitions("Auto,Auto");
                Grid.SetColumn(label, 0);
                Grid.SetColumn(control, 0);
                Grid.SetRow(label, 0);
                Grid.SetRow(control, 1);
                control.Margin = new Thickness(controlMargin.Left, controlMargin.Top + 4, controlMargin.Right, controlMargin.Bottom + 4);
            }, () =>
            {
                g.RowDefinitions = new RowDefinitions();
                g.ColumnDefinitions = columns;
                Grid.SetRow(label, 0);
                Grid.SetRow(control, 0);
                Grid.SetColumn(label, 0);
                Grid.SetColumn(control, 1);
                control.Margin = controlMargin;
            });
        }

        private static void Change(Control c, List<Control> done, Action apply, Action back)
        {
            undo.AddOrUpdate(c, back);
            done.Add(c);
            apply();
        }
    } // internal static class PhoneSheet
}
