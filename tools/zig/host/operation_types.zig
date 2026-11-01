//! Request-arena operation intents. Decoding does not allocate ordinary new-member IDs;
//! preparation allocates recipe graphs, then the transaction materializes ordinary members.
const model = @import("model_reader");
const recipe = @import("recipe.zig");

pub const Target = enum { function, type, field, variant, parameter, effect, capability, capabilityOperation, capabilityParameter };
pub const Rename = struct { target: Target, id: []const u8, name: []const u8 };
pub const Visibility = struct { target: Target, id: []const u8, value: bool };
pub const Attribute = enum { mayWait, taskBound, mustNotWait };
pub const Flag = struct { attribute: Attribute, id: []const u8, value: bool };
pub const Field = struct { name: []const u8, type: recipe.TypeRef };
pub const CallPlan = union(enum) { bind: struct { call: []const u8, value: []const u8 }, leave_missing: []const u8 };
pub const ValuePlan = struct { construct: []const u8, value: union(enum) { existing: []const u8, recipe: *const recipe.Node } };
pub const Implementation = struct { construct: []const u8, function: []const u8 };
pub const ArmRecipe = struct { match: []const u8, body: *const recipe.Node };
pub const RequiredEffect = struct { function: []const u8, effect: []const u8 };
pub const Intent = union(enum) {
    rename: Rename,
    set_exported: Visibility,
    set_attribute: Flag,
    add_function_parameter: struct { function: []const u8, field: Field, calls: []const CallPlan },
    add_product_field: struct { type: []const u8, field: Field, constructions: []const ValuePlan },
    add_capability_operation: struct { capability: []const u8, name: []const u8, parameters: []const Field, result: recipe.TypeRef, effect: []const u8, may_wait: bool, implementations: []const Implementation },
    // Variant and field IDs are already allocated during wire decoding.
    add_union_variant: struct { type: []const u8, variant: model.Model, arms: []const ArmRecipe },
    remove_product_field: []const u8,
    remove_function_parameter: []const u8,
    remove_union_variant: []const u8,
    delete_function: []const u8,
    delete_type: []const u8,
    delete_effect: []const u8,
    delete_capability: []const u8,
    delete_capability_operation: []const u8,
    resolve_missing_argument: struct { obligation: []const u8, value: []const u8 },
    add_required_effect: RequiredEffect,
    remove_required_effect: RequiredEffect,
};
