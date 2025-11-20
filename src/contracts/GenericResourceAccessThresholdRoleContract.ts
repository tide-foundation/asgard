import { Policy } from "../models/Policy";
import { BaseContract } from "./BaseContract";

export class GenericResourceAccessThresholdRoleContract extends BaseContract{
    public id: string = "CardanoTx";
    protected test(policy: Policy): void {
        let successfulDokens = 0;
        this.dokens.forEach(d => {
            if(d.hasResourceAccessRole(policy.params.entries.get("role"), policy.params.entries.get("clientId"))) successfulDokens++;
        })
        const threshold = policy.params.entries.get("threshold");
        if(!threshold) throw 'Threshold must be provided';
        if(successfulDokens < threshold) throw 'Not enough successful dokens with requires roles/clients';
    }
}