---
description: "Why an Avalonia style override is silently ignored: Template priority and Fluent's PART_ContentPresenter fills."
paths:
  - "**/*.axaml"
  - "**/*.axaml.cs"
---

# Avalonia styling traps

**Template priority beats Style priority.** A value a ControlTheme sets inside
its template (an inline `TemplateBinding`, *or* a setter in a nested
`<Style Selector="^ /template/ Part#Name">` within the theme) is applied at
`BindingPriority.Template`, which outranks an app-level `Style`. So an app style
with `/template/ Part#Name` that targets the same property is ignored with no
error. This is the opposite of WPF. Ways out, in order:

1. A selector with an activator (`TreeViewItem:not(:disabled) /template/ …`)
   gets StyleTrigger priority and wins. The tree chevron margin uses this (see
   CLAUDE.md, UI design rules).
2. Target a property the template leaves at its default (e.g. a nested
   `PART_ItemsPresenter`'s `Margin`), where a plain style wins.
3. Set it as a LocalValue from code-behind on every template apply:
   `x.TemplateApplied += (_, e) => { if (e.NameScope.Find<Control>("PART_x") is {} p) Grid.SetRowSpan(p, 1); };`
   LocalValue outranks Template, and the handler re-runs on every template
   reapply (a theme switch), so the value sticks.

**Checked/selected/hover fills live on the inner `PART_ContentPresenter`.**
Fluent paints `ToggleButton:checked`, `ListBoxItem:selected` and friends on
`/template/ ContentPresenter#PART_ContentPresenter`, so a `Background` setter on
the control itself renders but is covered. Override the part with a more
specific selector (`ToggleButton.toolbar:checked /template/ ContentPresenter#PART_ContentPresenter`)
and pin `:checked:pointerover` and `:checked:pressed` too, or the fill flips
back to Fluent's grey on hover. A plain `Button` with a class has no such
override at rest; only its `:pointerover` needs the part pin.

When checking a fill in a test, read the part's `Background` (and the brush's
`.Opacity`: `SolidColorBrush.ToString()` omits it). Asserting the CSS class does
not catch this.
