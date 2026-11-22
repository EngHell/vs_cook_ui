using System.Collections.Generic;

namespace CookUi;

/// <summary>
/// Where a recipe window gets its recipes from: vanilla's cooking recipes, a mod's own
/// registry (A Culinary Artillery's mixing or simmering recipes)... Asked again each time
/// the station's contents change, so a provider may read a live list.
/// </summary>
public interface IRecipeProvider<out TRecipe>
{
    IEnumerable<TRecipe> GetRecipes();
}
