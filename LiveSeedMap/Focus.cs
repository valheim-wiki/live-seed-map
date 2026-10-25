using System.Collections;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace LiveSeedMap
{
    /// <summary>What the player points at when pressing the show-on-map key: an item or a creature, by prefab name.</summary>
    public sealed class FocusTarget { public string Kind, Prefab; }

    /// <summary>
    /// Reads the thing under the player's pointer the way the game's own panels find it. With the inventory open:
    /// the slot under the pointer in the player's grid, then in an open container's grid (InventoryGrid.GetItem, or
    /// GetGamepadSelectedItem with a gamepad), then the recipe row under the pointer in the crafting list, then the
    /// selected recipe (InventoryGui.m_availableRecipes, m_selectedRecipe, private RecipeDataPair read by reflection).
    /// Without it: the creature under the crosshair (Player.GetHoverCreature), then an item lying on the ground
    /// (Player.GetHoverObject with an ItemDrop). The item tooltip (UITooltip) carries only localized text, no item, so the
    /// slot under the pointer that raised it is what names the item.
    /// </summary>
    public static class Focus
    {
        static readonly FieldInfo SelectedRecipeField = AccessTools.Field(typeof(InventoryGui), "m_selectedRecipe");
        static readonly FieldInfo AvailableRecipesField = AccessTools.Field(typeof(InventoryGui), "m_availableRecipes");
        static readonly FieldInfo ContainerGridField = AccessTools.Field(typeof(InventoryGui), "m_containerGrid");

        /// <summary>The player is typing: chat, console, a text prompt or a pin name on the large map.</summary>
        public static bool Typing() =>
            (Chat.instance && Chat.instance.HasFocus()) || global::Console.IsVisible() || TextInput.IsVisible() || Minimap.InTextInput() || Menu.IsVisible();

        public static FocusTarget Find()
        {
            var player = Player.m_localPlayer;
            if (!player) return null;
            var gui = InventoryGui.instance;
            if (gui && InventoryGui.IsVisible())
            {
                var pointer = ZInput.pointerPosition;
                var cursor = new Vector2i((int)pointer.x, (int)pointer.y);
                var gamepad = ZInput.IsExclusiveGamepadActive();
                var grids = new[] { gui.m_playerGrid, gui.IsContainerOpen() ? ContainerGridField?.GetValue(gui) as InventoryGrid : null };
                foreach (var grid in grids)
                {
                    if (!grid) continue;
                    var item = gamepad ? (grid.m_uiGroup && grid.m_uiGroup.IsActive ? grid.GetGamepadSelectedItem() : null) : grid.GetItem(cursor);
                    var prefab = ItemPrefab(item);
                    if (prefab != null) return new FocusTarget { Kind = "item", Prefab = prefab };
                }
                var hovered = gamepad ? null : HoveredRecipe(gui, pointer);
                var recipe = hovered ?? RecipeOf(SelectedRecipeField?.GetValue(gui));
                if (recipe && recipe.m_item) return new FocusTarget { Kind = "item", Prefab = recipe.m_item.gameObject.name };
                return null;
            }
            var creature = player.GetHoverCreature();
            if (creature && !creature.IsPlayer()) return new FocusTarget { Kind = "creature", Prefab = global::Utils.GetPrefabName(creature.gameObject) };
            var hover = player.GetHoverObject();
            var drop = hover ? hover.GetComponentInParent<ItemDrop>() : null;
            if (drop) return new FocusTarget { Kind = "item", Prefab = global::Utils.GetPrefabName(drop.gameObject) };
            return null;
        }

        static Recipe HoveredRecipe(InventoryGui gui, Vector3 pointer)
        {
            if (!(AvailableRecipesField?.GetValue(gui) is IList rows)) return null;
            foreach (var row in rows)
            {
                var element = Property(row, "InterfaceElement") as GameObject;
                if (!element || !element.activeInHierarchy) continue;
                var rect = element.transform as RectTransform;
                if (rect && RectTransformUtility.RectangleContainsScreenPoint(rect, pointer)) return RecipeOf(row);
            }
            return null;
        }

        static Recipe RecipeOf(object pair) => pair == null ? null : Property(pair, "Recipe") as Recipe;

        static object Property(object row, string name) => row?.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(row);

        /// <summary>ItemData.m_dropPrefab is the ObjectDB prefab; an item without one is looked up by its shared name.</summary>
        static string ItemPrefab(ItemDrop.ItemData item)
        {
            if (item == null) return null;
            if (item.m_dropPrefab) return item.m_dropPrefab.name;
            var match = ObjectDB.instance ? ObjectDB.instance.m_items.FirstOrDefault(go => go && go.GetComponent<ItemDrop>() is ItemDrop drop && drop.m_itemData.m_shared.m_name == item.m_shared.m_name) : null;
            return match ? match.name : null;
        }
    }
}
