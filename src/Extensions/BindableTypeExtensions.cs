// FOBO fork addition.
//
// The one place a Type crosses from "whatever the app put in ItemsSource" into this library's
// reflection, and the one place that crossing is signed off.
//
// This library binds by property NAME: a column carries a path string out of XAML and the binder
// resolves it against the item's runtime type. The trimmer cannot follow that — the name is data —
// so every GetProperty/GetProperties/GetMethod/GetInterfaces call in the binder used to warn, and
// the fork answered by putting IL2070 and IL2075 in NoWarn for the whole project. That hid the
// warnings without changing what happens under a trimmed or AOT build, which is that a property
// nobody referenced statically is gone and the cell reading it comes back empty.
//
// The warnings are real, so they are back on. What they were pointing at is not fixable by
// annotating each call site: annotations propagate UP, and every one of these chains ends at
// Expression.Type or Object.GetType(), neither of which can be annotated. So the requirement is
// stated once, here, and the rest of the binder carries proper annotations that lead to it.
//
// WHAT A CONSUMER HAS TO DO. Nothing, for a JIT build. For a trimmed or NativeAOT build the item
// types' properties have to survive, and this library cannot keep them alive on the app's behalf
// because it never names them. The app does, by one of:
//
//   * [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] on the item
//     type, or on whatever generic parameter carries it;
//   * [WinRT.GeneratedBindableCustomProperty] on the item type, which is the WinUI answer and also
//     removes the reflection at the binding layer above this one;
//   * a TrimmerRootDescriptor naming the model assembly, which is the blunt one.

using System;
using System.Diagnostics.CodeAnalysis;

namespace WinUI.TableView.Extensions;

/// <summary>
/// FOBO fork addition. Marks a type as one this library is about to read members off by name.
/// </summary>
internal static class BindableTypeExtensions
{
    /// <summary>
    /// The members the binder reads off a data item's type. Every one of these is reached by name
    /// rather than by a static reference: properties for path segments and indexers, methods for a
    /// dictionary's TryGetValue, interfaces for working out what a container is.
    /// </summary>
    internal const DynamicallyAccessedMemberTypes BindableMembers =
        DynamicallyAccessedMemberTypes.PublicProperties
        | DynamicallyAccessedMemberTypes.NonPublicProperties
        | DynamicallyAccessedMemberTypes.PublicMethods
        | DynamicallyAccessedMemberTypes.Interfaces;

    /// <summary>
    /// The same type, now declared to carry <see cref="BindableMembers"/>.
    ///
    /// <para>Called where a type arrives from somewhere the trimmer cannot annotate — an
    /// <see cref="System.Linq.Expressions.Expression"/>'s own Type, an
    /// <see cref="object.GetType()"/>, an interface picked out of GetInterfaces(). It does nothing
    /// at runtime. Its whole job is to be the single line where "the trimmer cannot prove this"
    /// is admitted, so that the admission is one reviewable place instead of a project-wide NoWarn.
    /// </para>
    /// </summary>
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2068:Return type does not satisfy DynamicallyAccessedMembersAttribute requirements",
        Justification =
            "The binder resolves member names that arrive as data (a column's binding path), so no " +
            "annotation can reach the item type from here. Preserving those members is the " +
            "consuming app's job; see the file header for the three supported ways to do it. " +
            "Stated once here so the binder's own call sites carry real annotations.")]
    [return: DynamicallyAccessedMembers(BindableMembers)]
    internal static Type ForBinding(this Type type) => type;
}
