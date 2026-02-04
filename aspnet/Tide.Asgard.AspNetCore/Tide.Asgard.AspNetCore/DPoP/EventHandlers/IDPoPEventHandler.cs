// Copyright (c) Okta, Inc. and/or its affiliates. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE-APACHE-2.0 in the project root.
// Modifications Copyright (c) Tide Foundation Limited.

﻿namespace Tide.Asgard.AspNetCore.Authentication.DPoP.EventHandlers;

public interface IDPoPEventHandler<T>
{
	/// <summary>
	///     Handles the event with the provided context.
	/// </summary>
	/// <param name="context">Context based on the event, like <see cref="MessageReceivedContext" /></param>
	/// <returns></returns>
	Task Handle(T context);
}

