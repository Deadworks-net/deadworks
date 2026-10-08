using DeadworksManaged.Game;

// In the API's namespace so the members appear on its types without another using directive.
namespace DeadworksManaged.Api;

/// <summary>Views of the curated wrappers as generated schema classes.</summary>
public static class SchemaViewExtensions {
	extension(NativeEntity self) {
		/// <summary>
		/// This object as the schema class <typeparamref name="T"/>. Nothing checks that it is one.
		/// Use it for a wrapper with no <c>.Schema</c> of its own, or when you know the object is a
		/// more derived class than its wrapper: <c>ability.SubclassVData?.AsSchema&lt;Schema.CitadelAbilityVData&gt;()</c>.
		/// </summary>
		public T AsSchema<T>() where T : SchemaObject, ISchemaClass<T>
			=> self is CBaseEntity entity ? SchemaObject.ForEntityHandle<T>(entity.EntityHandle) : SchemaObject.At<T>(self.Handle);
	}
}
