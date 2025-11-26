import { Policy } from "../models/Policy";
import { BaseContract } from "./BaseContract";

export class GenericRealmAccessThresholdRoleContract extends BaseContract{
    public id: string = "GenericRealmAccessThresholdRoleContract";
    protected async test(policy: Policy): Promise<void> {
        let successfulDokens = 0;
        this.dokens.forEach(d => {
            if(d.hasRealmAccessRole(policy.params.getParameter<string>("role"))) successfulDokens++;
        })
        const threshold = policy.params.getParameter<number>("threshold");
        if(successfulDokens < threshold) throw 'Not enough successful dokens with requires roles/clients';
    }
}