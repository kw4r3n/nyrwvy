namespace Nayamnyam.Spike

module Scenarios =
    let initialSnapshot =
        {
            SchemaVersion = 1
            Id = SnapshotId.create "snapshot:initial"
            Types =
                [
                    {
                        Id = TypeId.create "type:raw-user-input"
                        Name = "raw_user_input"
                    }
                    {
                        Id = TypeId.create "type:string"
                        Name = "string"
                    }
                    {
                        Id = TypeId.create "type:valid-user"
                        Name = "valid_user"
                    }
                ]
            Functions =
                [
                    {
                        Id = FunctionId.create "function:main"
                        Name = "main"
                        Parameters = []
                    }
                    {
                        Id =
                            FunctionId.create
                                "function:validate-user"
                        Name = "validate_user"
                        Parameters =
                            [
                                {
                                    Id =
                                        ParameterId.create
                                            "parameter:input"
                                    Name = "input"
                                    TypeId =
                                        TypeId.create
                                            "type:raw-user-input"
                                }
                            ]
                    }
                ]
            Calls =
                [
                    {
                        Id =
                            CallId.create
                                "call:main-validate"
                        CalleeId =
                            FunctionId.create
                                "function:validate-user"
                        Arguments =
                            [
                                {
                                    ParameterId =
                                        ParameterId.create
                                            "parameter:input"
                                    ValueRef = "local:raw-input"
                                }
                            ]
                    }
                ]
            Obligations = []
        }
