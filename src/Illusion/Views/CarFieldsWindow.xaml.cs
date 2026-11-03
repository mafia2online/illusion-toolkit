using System.Globalization;
using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using Illusion.Assets.Cars;

namespace Illusion.Views;

/// <summary>
/// The numbers a marker or one of a component's own prefab rows carries, edited where the row sits.
///
/// <para>
/// The window is BUILT from the fields it is given rather than spelled out, because they differ per role: a
/// seat carries its number, its type, its group and where the occupant sits; a climb box two corners; a
/// window a depth and whether it rolls down; an axle a type and three masses. Each field carries its own
/// label, its own hint and the address it is written back through, so nothing here has to know which of the
/// prefab's four parallel lists is being edited — which is the whole point of the aggregate.
/// </para>
/// </summary>
public sealed partial class CarFieldsWindow : Window
{
    private readonly List<Row> _rows = [];
    private IReadOnlyList<CarField> _fields = [];
    private string _title = "";
    private string _caption = "";

    /// <summary>
    /// One field on screen: what it was, the boxes the modder types into, and the text those boxes were
    /// SHOWN with.
    ///
    /// <para>
    /// The shown text is what makes a field the modder did not touch come back untouched. A box is rendered
    /// to four decimals, so re-reading it turns a seat sitting at 0.317383 into one at 0.3174 — and because
    /// the aggregate moves a marker when its row says it has moved, opening this window and pressing Apply
    /// would drag the marker and rewrite the frame resource for an edit nobody made.
    /// </para>
    /// </summary>
    private sealed record Row(
        CarField Field, TextBox[] Boxes, CheckBox? Flag, DropDownField? Picker, string[] Shown);

    /// <param name="title">What is being edited — "Seat 2 on doorFL".</param>
    /// <param name="caption">The line above the fields: what this row IS.</param>
    /// <param name="fields">The row's own fields, as the aggregate read them.</param>
    public CarFieldsWindow(string title, string caption, IReadOnlyList<CarField> fields)
    {
        InitializeComponent();
        Setup(title, caption, fields);
    }

    /// <summary>
    /// The same question again, holding the answers that were given and the reason they were refused.
    ///
    /// <para>
    /// A WPF dialog cannot be shown twice, so this is a second window rather than the same one reopened. The
    /// point is that a modder whose numbers were refused does not have to type them all again to find out
    /// whether the next guess is any better.
    /// </para>
    /// </summary>
    public CarFieldsWindow Again(string refusal)
    {
        var again = new CarFieldsWindow(_title, _caption, Values.Count > 0 ? Values : _fields)
        {
            Owner = Owner,
        };
        again.Fail(refusal);
        return again;
    }

    /// <summary>The fields as the modder left them, once the dialog was accepted.</summary>
    public IReadOnlyList<CarField> Values { get; private set; } = [];

    private void Setup(string title, string caption, IReadOnlyList<CarField> fields)
    {
        _title = title ?? "";
        _caption = caption ?? "";
        _fields = fields ?? [];

        Title = _title;
        Caption.Text = _caption;
        FootNote.Text = "Written into the working copy. Then Build.";

        foreach (CarField field in _fields) Fields.Children.Add(Build(field));
    }

    /// <summary>One field, in the shape its kind wants: three boxes for a point, a check box for a flag, one
    /// box for everything else — with its own hint under it, because a number whose meaning is the game's is
    /// no use without the sentence that says what it does.</summary>
    private UIElement Build(CarField field)
    {
        var stack = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        stack.Children.Add(new TextBlock
        {
            Text = field.Label,
            Style = (Style)FindResource("DimLabel"),
            Margin = new Thickness(0, 0, 0, 4),
        });

        TextBox[] boxes = [];
        CheckBox? flag = null;
        DropDownField? picker = null;
        if (field.Kind == CarFieldKind.Frame)
        {
            picker = Picker(field);
            stack.Children.Add(picker);
        }
        else if (field.Kind == CarFieldKind.Flag)
        {
            flag = new CheckBox { IsChecked = field.Number != 0f, Content = "Yes" };
            stack.Children.Add(flag);
        }
        else if (field.Kind == CarFieldKind.Point)
        {
            boxes = [Box(field.Point.X), Box(field.Point.Y), Box(field.Point.Z)];
            stack.Children.Add(Triple(boxes));
        }
        else
        {
            boxes = [Box(field.Number)];
            stack.Children.Add(boxes[0]);
        }

        if (field.Hint.Length > 0)
        {
            stack.Children.Add(new TextBlock
            {
                Text = field.Hint,
                Style = (Style)FindResource("DimLabel"),
                Margin = new Thickness(0, 4, 0, 0),
                TextWrapping = TextWrapping.Wrap,
            });
        }

        _rows.Add(new Row(field, boxes, flag, picker, [.. boxes.Select(b => b.Text)]));
        return stack;
    }

    /// <summary>
    /// The frames this reference may be pointed at, as a list to choose from — never a box to type a hash
    /// into, because a hash that names nothing does not fail: the part simply stops working, silently.
    ///
    /// <para>
    /// A reference that already names nothing keeps its own entry at the top so it can be SEEN and replaced.
    /// Dropping it would leave the picker blank and make a dangling reference look like an empty slot, which
    /// is the one distinction this row exists to draw.
    /// </para>
    /// </summary>
    private DropDownField Picker(CarField field)
    {
        List<CarFrameChoice> items = [.. field.Choices];
        CarFrameChoice? current = items.FirstOrDefault(c => c.Hash == field.Frame);
        if (current == null)
        {
            current = new CarFrameChoice(field.Frame, field.FrameName + "  (no frame of this archive)");
            items.Insert(0, current);
        }
        return new DropDownField
        {
            DisplayPath = nameof(CarFrameChoice.Name),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = items,
            SelectedItem = current,
        };
    }

    private TextBox Box(float value) => new()
    {
        Style = (Style)FindResource("DarkBox"),
        Text = value.ToString("0.####", CultureInfo.InvariantCulture),
    };

    private static UIElement Triple(TextBox[] boxes)
    {
        var grid = new Grid();
        for (int i = 0; i < 5; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = i % 2 == 0 ? new GridLength(1, GridUnitType.Star) : new GridLength(10),
            });
        }
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        string[] axes = ["X", "Y", "Z"];
        for (int i = 0; i < 3; i++)
        {
            Grid.SetColumn(boxes[i], i * 2);
            grid.Children.Add(boxes[i]);
            var label = new TextBlock
            {
                Text = axes[i],
                Margin = new Thickness(1, 4, 0, 0),
            };
            Grid.SetRow(label, 1);
            Grid.SetColumn(label, i * 2);
            grid.Children.Add(label);
        }
        return grid;
    }

    private void Accept_Click(object sender, RoutedEventArgs e)
    {
        var values = new List<CarField>(_rows.Count);
        foreach (Row row in _rows)
        {
            if (row.Picker != null)
            {
                // A picker holds a frame the archive HAS, so there is nothing to parse and nothing to refuse.
                // Its unchanged state is the same object it was given, which goes back as it came.
                ulong picked = row.Picker.SelectedItem is CarFrameChoice choice ? choice.Hash : row.Field.Frame;
                values.Add(picked == row.Field.Frame ? row.Field : row.Field with { Frame = picked });
                continue;
            }
            // A box still holding the text it was shown with is a field nobody typed into, and it goes back
            // exactly as it came — see Row.Shown for why re-reading it instead is an edit of its own.
            if (row.Boxes.Length > 0 && row.Boxes.Zip(row.Shown).All(
                    p => string.Equals(p.First.Text, p.Second, StringComparison.Ordinal)))
            {
                values.Add(row.Field);
                continue;
            }
            if (row.Flag != null)
            {
                values.Add(row.Field with { Number = row.Flag.IsChecked == true ? 1f : 0f });
                continue;
            }
            if (row.Field.Kind == CarFieldKind.Point)
            {
                // PER AXIS, not per row. A point is three boxes and the check above is all-or-nothing, so
                // nudging X alone would re-read Y and Z from their own four-decimal text and move them too:
                // a centre of mass at 0.317383 becomes 0.3174 because the modder typed in the box beside it.
                Vector3 point = row.Field.Point;
                for (int axis = 0; axis < 3; axis++)
                {
                    if (string.Equals(row.Boxes[axis].Text, row.Shown[axis], StringComparison.Ordinal)) continue;
                    if (!Read(row.Boxes[axis], out float typed))
                    {
                        Fail($"Every part of \"{row.Field.Label}\" has to be a number.");
                        return;
                    }
                    point = axis switch
                    {
                        0 => point with { X = typed },
                        1 => point with { Y = typed },
                        _ => point with { Z = typed },
                    };
                }
                values.Add(row.Field with { Point = point });
                continue;
            }
            if (!Read(row.Boxes[0], out float number))
            {
                Fail($"\"{row.Field.Label}\" has to be a number.");
                return;
            }
            if (row.Field.Kind == CarFieldKind.Count && number < 0f)
            {
                Fail($"\"{row.Field.Label}\" cannot be negative.");
                return;
            }
            values.Add(row.Field with
            {
                Number = row.Field.Kind == CarFieldKind.Count ? MathF.Round(number) : number,
            });
        }

        Values = values;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    /// <summary>Says what the aggregate refused, in the window rather than as a notice behind it — the modder
    /// is still standing in front of the numbers that caused it.</summary>
    public void Fail(string message)
    {
        Caption.Text = _caption.Length > 0 ? _caption + "\n\n" + message : message;
        Caption.Foreground = Palette.StatusError;
    }

    // Accepts both separators: a decimal comma is what a Russian or a German keyboard produces, and rejecting
    // "0,3" as "not a number" would be the dialog being pedantic about something it can simply understand.
    private static bool Read(TextBox field, out float value) =>
        float.TryParse(field.Text?.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture,
            out value);
}
