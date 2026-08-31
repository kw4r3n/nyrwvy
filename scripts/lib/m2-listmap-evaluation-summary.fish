function m2_listmap_artifact_set_is_durable --argument-names run_dir
    for required_nonempty in run-state.json run-metadata.json metrics.json events.jsonl
        if not test -s "$run_dir/$required_nonempty"
            return 1
        end
    end

    # Expected-rejection tasks have no harness-owned perturbation or commit
    # verification event, so an existing empty harness log is valid evidence.
    if not test -f "$run_dir/harness-events.jsonl"
        return 1
    end

    return 0
end
