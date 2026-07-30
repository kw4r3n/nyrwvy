using CallId = Nayamnyam.Spike.StableId<Nayamnyam.Spike.CallTag>;
using FunctionId = Nayamnyam.Spike.StableId<Nayamnyam.Spike.FunctionTag>;
using ParameterId = Nayamnyam.Spike.StableId<Nayamnyam.Spike.ParameterTag>;
using SnapshotId = Nayamnyam.Spike.StableId<Nayamnyam.Spike.SnapshotTag>;
using TypeId = Nayamnyam.Spike.StableId<Nayamnyam.Spike.TypeTag>;

namespace Nayamnyam.Spike;

public static class Scenarios
{
    public static Snapshot InitialSnapshot { get; } = CreateInitialSnapshot();

    private static Snapshot CreateInitialSnapshot()
    {
        var inputParameter = new Parameter(
            new ParameterId("parameter:input"),
            "input",
            new TypeId("type:raw-user-input"));

        return new Snapshot(
            1,
            new SnapshotId("snapshot:initial"),
            [
                new TypeEntity(
                    new TypeId("type:raw-user-input"),
                    "raw_user_input"),
                new TypeEntity(new TypeId("type:string"), "string"),
                new TypeEntity(new TypeId("type:valid-user"), "valid_user"),
            ],
            [
                new FunctionEntity(
                    new FunctionId("function:main"),
                    "main",
                    []),
                new FunctionEntity(
                    new FunctionId("function:validate-user"),
                    "validate_user",
                    [inputParameter]),
            ],
            [
                new CallEntity(
                    new CallId("call:main-validate"),
                    new FunctionId("function:validate-user"),
                    [
                        new ArgumentBinding(
                            inputParameter.Id,
                            "local:raw-input"),
                    ]),
            ],
            []);
    }
}
